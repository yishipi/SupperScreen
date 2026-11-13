using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MagicCompanion.Agent;

internal sealed class HostIdentity : IDisposable
{
    public readonly string HostId;
    public readonly ECDsa Key;
    public string Fingerprint =>
        "sha256:"
        + Convert
            .ToBase64String(SHA256.HashData(Key.ExportSubjectPublicKeyInfo()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public HostIdentity(AgentStore store, string directory)
    {
        HostId = store.GetOrCreate("hostId", () => "host_" + Guid.NewGuid().ToString("N"));
        var keyFile = Path.Combine(directory, "identity.dpapi");
        if (File.Exists(keyFile))
        {
            Key = ECDsa.Create();
            Key.ImportPkcs8PrivateKey(
                ProtectedData.Unprotect(
                    File.ReadAllBytes(keyFile),
                    null,
                    DataProtectionScope.CurrentUser
                ),
                out _
            );
        }
        else
        {
            Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var encrypted = ProtectedData.Protect(
                Key.ExportPkcs8PrivateKey(),
                null,
                DataProtectionScope.CurrentUser
            );
            File.WriteAllBytes(keyFile + ".tmp", encrypted);
            File.Move(keyFile + ".tmp", keyFile);
        }
    }

    public X509Certificate2 CreateCertificate(IEnumerable<IPAddress> addresses)
    {
        var request = new CertificateRequest("CN=" + HostId, Key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        foreach (var address in addresses)
            san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
        );
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2)
        );
        // Schannel needs a user key container on this Windows configuration. The import owns and cleans up its temporary container.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.UserKeySet
        );
    }

    public static IPAddress[] LocalAddresses()
    {
        return NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
            )
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPrivate(a)
            )
            .Distinct()
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }

    public void Dispose()
    {
        Key.Dispose();
    }
}
