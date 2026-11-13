using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagicCompanion.Core;

public sealed record PairingSession(string Id, byte[] Secret, long Started, string ExpiresAt);

public sealed record PairingRequest(string DeviceName, string DeviceId, string ShortCode);

public sealed class PairingCoordinator(AgentStore store, string hostId)
{
    private readonly object gate = new();
    private PairingSession? session;
    private int attempts;
    private string? pendingSessionId;
    public Func<PairingRequest, CancellationToken, Task<string[]?>>? ApproveAsync { get; set; }

    public PairingSession Begin()
    {
        lock (gate)
        {
            session = new(
                Wire.NewId(),
                RandomNumberGenerator.GetBytes(32),
                Stopwatch.GetTimestamp(),
                DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")
            );
            attempts = 0;
            pendingSessionId = null;
            return session;
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            session = null;
            pendingSessionId = null;
        }
    }

    public void Revoke(string deviceId)
    {
        lock (gate)
        {
            if (store.Device(deviceId) is not { Revoked: false } device)
                return;
            Cancel();
            store.SaveDevice(device with { Revoked = true, Permissions = [] });
        }
    }

    public bool Active
    {
        get
        {
            lock (gate)
                return session != null
                    && Stopwatch.GetElapsedTime(session.Started).TotalSeconds < 120;
        }
    }

    public async Task<TrustedDevice> AuthenticateAsync(
        JsonElement message,
        string linkId,
        string nonce,
        bool allowPairing,
        CancellationToken cancellation
    )
    {
        var deviceId = message.RequiredString("deviceId", 128);
        var publicKeyText = message.RequiredString("publicKey", 1024);
        byte[] publicKey;
        byte[] signature;
        try
        {
            publicKey = Convert.FromBase64String(publicKeyText);
            signature = Convert.FromBase64String(message.RequiredString("signature", 1024));
        }
        catch (FormatException)
        {
            throw new AgentException("AUTH_FAILED", "身份格式无效");
        }
        if (Wire.Fingerprint(publicKey) != deviceId)
            throw new AgentException("AUTH_FAILED", "设备身份不匹配");
        var challenge = Encoding.UTF8.GetBytes(Wire.ChallengeText(hostId, linkId, nonce, deviceId));
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (
                read != publicKey.Length
                || key.KeySize != 256
                || !key.VerifyData(
                    challenge,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence
                )
            )
                throw new AgentException("AUTH_FAILED", "设备签名无效");
        }
        catch (CryptographicException)
        {
            throw new AgentException("AUTH_FAILED", "无法验证设备密钥");
        }
        var known = store.Device(deviceId);
        var wantsPairing =
            allowPairing
            && message.TryGetProperty("pairSessionId", out var pairId)
            && pairId.ValueKind != JsonValueKind.Null;
        if (known != null && known.PublicKey != publicKeyText)
            throw new AgentException("AUTH_FAILED", "设备密钥已改变");
        if (!wantsPairing && known != null)
        {
            if (known.Revoked)
                throw new AgentException("DEVICE_REVOKED", "电脑已解除此设备配对");
            return known;
        }
        if (!allowPairing)
            throw new AgentException("NOT_PAIRED", "媒体连接必须先完成设备配对");
        PairingSession current;
        lock (gate)
        {
            if (!Active || session == null || pendingSessionId != null)
                throw new AgentException("NOT_PAIRED", "请在电脑重新打开配对二维码");
            if (++attempts > 5)
            {
                session = null;
                throw new AgentException("AUTH_FAILED", "配对尝试次数已超限");
            }
            current = session;
            byte[] proof;
            try
            {
                proof = Convert.FromBase64String(message.RequiredString("pairProof", 1024));
            }
            catch (FormatException)
            {
                throw new AgentException("AUTH_FAILED", "配对证明无效");
            }
            if (
                message.RequiredString("pairSessionId", 128) != current.Id
                || !CryptographicOperations.FixedTimeEquals(
                    HMACSHA256.HashData(current.Secret, challenge),
                    proof
                )
            )
                throw new AgentException("AUTH_FAILED", "二维码已失效或证明不匹配");
            if (store.Device() is { Revoked: false } active && active.DeviceId != deviceId)
                throw new AgentException("DEVICE_BUSY", "电脑已有配对手机，请先解除原配对");
            pendingSessionId = current.Id;
        }
        try
        {
            var name = message.RequiredString("deviceName", 80);
            var code = Convert.ToHexString(SHA256.HashData(challenge))[..6];
            var permissions =
                ApproveAsync == null
                    ? null
                    : await ApproveAsync(new(name, deviceId, code), cancellation);
            lock (gate)
            {
                if (permissions == null && session?.Id == current.Id)
                    session = null;
                if (session?.Id != current.Id || !Active || permissions == null)
                    throw new AgentException("AUTH_FAILED", "配对被拒绝、取消或已过期");
                var device = new TrustedDevice(
                    deviceId,
                    name,
                    publicKeyText,
                    PermissionSet.Normalize(permissions),
                    false
                );
                store.SaveDevice(device);
                session = null;
                return device;
            }
        }
        finally
        {
            lock (gate)
                if (pendingSessionId == current.Id)
                    pendingSessionId = null;
        }
    }
}

public static class JsonInput
{
    public static string RequiredString(
        this JsonElement element,
        string property,
        int maximum = 2048
    )
    {
        if (
            !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || text.Length == 0
            || text.Length > maximum
        )
            throw new AgentException("INVALID_ARGUMENT", $"字段 {property} 无效");
        return text;
    }

    public static int Integer(this JsonElement element, string property, int min, int max)
    {
        if (
            !element.TryGetProperty(property, out var value)
            || !value.TryGetInt32(out var result)
            || result < min
            || result > max
        )
            throw new AgentException("INVALID_ARGUMENT", $"字段 {property} 超出范围");
        return result;
    }

    public static bool Boolean(this JsonElement element, string property)
    {
        if (
            !element.TryGetProperty(property, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
            throw new AgentException("INVALID_ARGUMENT", $"字段 {property} 必须为布尔值");
        return value.GetBoolean();
    }
}
