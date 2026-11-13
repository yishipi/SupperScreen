using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagicCompanion.Core;
using Xunit;

namespace MagicCompanion.Tests;

public sealed class StoreAndPairingTests
{
    [Fact]
    public void TodoColorsMigrateLegacyDataAndSurviveEditsUndoAndRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "superscreen-colors-" + Wire.NewId());
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "legacy.db");
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
            {
                legacy.Open();
                using var schema = legacy.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE todos(id TEXT PRIMARY KEY,title TEXT NOT NULL,completed INTEGER NOT NULL,position INTEGER NOT NULL);
                    INSERT INTO todos VALUES('legacy','旧待办',0,0);
                    """;
                schema.ExecuteNonQuery();
            }
            using (var store = new AgentStore(file))
            {
                Assert.Null(store.Todos().Items.Single().Color);
                Assert.Null(store.Todos().Items.Single().TextColor);
                Assert.Null(store.Todos().Items.Single().BackgroundColor);
                var colored = store.ChangeTodo("color", "legacy", color: "#cf3c48");
                Assert.Equal("#CF3C48", colored.Items.Single().Color);
                Assert.Throws<AgentException>(() => store.ChangeTodo("color", "legacy", color: "#GGGGGG"));
                Assert.Equal(colored.Revision, store.Todos().Revision);
                store.ChangeTodo("edit", "legacy", "已改名", textColor: "#FFFFFF", backgroundColor: "#245BC2");
                store.ChangeTodo("setCompleted", "legacy", completed: true);
                store.ChangeTodo("delete", "legacy");
                var restored = store.ChangeTodo("undo").Items.Single();
                Assert.Equal("#CF3C48", restored.Color);
                Assert.Equal("已改名", restored.Title);
                Assert.True(restored.Completed);
                var wire = JsonSerializer.SerializeToElement(store.Todos(), Wire.Json);
                Assert.Equal("#CF3C48", wire.GetProperty("items")[0].GetProperty("color").GetString());
            }
            using var reopened = new AgentStore(file);
            Assert.Equal("#CF3C48", reopened.Todos().Items.Single().Color);
            Assert.Equal("#FFFFFF", reopened.Todos().Items.Single().TextColor);
            Assert.Equal("#245BC2", reopened.Todos().Items.Single().BackgroundColor);
            Assert.Null(reopened.ChangeTodo("color", "legacy").Items.Single().Color);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TextAndBackgroundColorsAreAtomicOnAddEditAndPersistAcrossUndo()
    {
        using var store = new AgentStore(":memory:");
        var added = store.ChangeTodo("add", title: "配色", textColor: "#aabbcc", backgroundColor: "#112233");
        var item = added.Items.Single();
        Assert.Equal("#AABBCC", item.TextColor);
        Assert.Equal("#112233", item.BackgroundColor);
        Assert.Throws<AgentException>(() => store.ChangeTodo("add", title: "无效", textColor: "red"));
        Assert.Throws<AgentException>(() => store.ChangeTodo("edit", item.Id, "修改", textColor: "#FFFFFF", backgroundColor: "#GGGGGG"));
        Assert.Equal(added.Revision, store.Todos().Revision);
        Assert.Equal("配色", store.Todos().Items.Single().Title);
        store.ChangeTodo("edit", item.Id, "修改", textColor: "#FFFFFF", backgroundColor: "#245BC2");
        store.ChangeTodo("delete", item.Id);
        var restored = store.ChangeTodo("undo").Items.Single();
        Assert.Equal("#FFFFFF", restored.TextColor);
        Assert.Equal("#245BC2", restored.BackgroundColor);
    }

    [Theory]
    [InlineData("tasks.run", "tasks.read")]
    [InlineData("todos.write", "todos.read")]
    public void PermissionDependenciesAreConsistent(string permission, string required)
    {
        var approved = PermissionSet.Normalize([permission, "unrecognized"]);
        Assert.Contains(permission, approved);
        Assert.Contains(required, approved);
        Assert.DoesNotContain("unrecognized", approved);
        var enabled = PermissionSet.Set([], permission, true);
        Assert.Equal(approved, enabled);
        var revoked = PermissionSet.Set(enabled, required, false);
        Assert.DoesNotContain(permission, revoked);
        Assert.DoesNotContain(required, revoked);
        Assert.Contains(required, PermissionSet.Set(enabled, permission, false));
    }

    [Fact]
    public async Task ExplicitPcRejectionInvalidatesTheQrSession()
    {
        using var store = new AgentStore(":memory:");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = new PairingCoordinator(store, "h");
        var session = pairing.Begin();
        pairing.ApproveAsync = (_, _) => Task.FromResult<string[]?>(null);
        await Assert.ThrowsAsync<AgentException>(
            () =>
                pairing.AuthenticateAsync(
                    Auth(key, "h", "l", "n", session),
                    "l",
                    "n",
                    true,
                    default
                )
        );
        Assert.False(pairing.Active);
        Assert.Null(store.Device());
    }

    [Fact]
    public void MaximumUnicodeTodoSnapshotFitsOrdinaryMessageBudget()
    {
        using var store = new AgentStore(":memory:");
        var title = string.Concat(Enumerable.Repeat("📚", 80));
        for (var i = 0; i < 100; i++)
            store.ChangeTodo("add", title: title);
        var response = Wire.Serialize(
            new
            {
                protocolVersion = 1,
                messageType = "response",
                requestId = Wire.NewId(),
                status = "ok",
                result = store.Todos(),
            }
        );
        Assert.True(Encoding.UTF8.GetByteCount(response) <= Wire.MaxMessageBytes);
    }

    [Theory]
    [InlineData("📚\\uD83D\\uDCDA")]
    [InlineData("\\\\\"📚\n中文")]
    [InlineData("plain")]
    public void Utf8WireEncodingPreservesLiteralEscapesAndUnicode(string value)
    {
        var encoded = Wire.Serialize(new { value });
        using var parsed = JsonDocument.Parse(encoded);
        Assert.Equal(value, parsed.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void TodoEditsAreAtomicOrderedAndUndoPreservesIdentity()
    {
        using var store = new AgentStore(":memory:");
        var added = store.ChangeTodo("add", title: "  完成报告 📚  ");
        var first = added.Items.Single();
        Assert.Equal("完成报告 📚", first.Title);
        Assert.Equal(1, added.Revision);
        store.ChangeTodo("add", title: "第二项");
        var moved = store.ChangeTodo("move", first.Id, direction: 1);
        Assert.Equal(first.Id, moved.Items[1].Id);
        var done = store.ChangeTodo("toggle", first.Id);
        Assert.True(done.Items[^1].Completed);
        var before = store.Todos();
        Assert.Throws<AgentException>(() => store.ChangeTodo("rename", first.Id, "\r\n"));
        Assert.Equal(before.Revision, store.Todos().Revision);
        store.ChangeTodo("delete", first.Id);
        var restored = store.ChangeTodo("undo");
        Assert.Equal(first.Id, restored.Items[^1].Id);
        Assert.True(restored.Revision > before.Revision);
        Assert.False(store.CanUndo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\t")]
    [InlineData("a\nb")]
    [InlineData("\uFFFD")]
    [InlineData("\ud800")]
    public void InvalidTitlesCannotCorruptStoredSnapshot(string title)
    {
        using var store = new AgentStore(":memory:");
        Assert.Throws<AgentException>(() => store.ChangeTodo("add", title: title));
        Assert.Empty(store.Todos().Items);
        Assert.Equal(0, store.Todos().Revision);
    }

    [Fact]
    public void TitleLimitCountsUnicodeScalarsAndCapacityIncludesCompleted()
    {
        using var store = new AgentStore(":memory:");
        var title = string.Concat(Enumerable.Repeat("📚", 80));
        var first = store.ChangeTodo("add", title: title).Items[0];
        Assert.Throws<AgentException>(() => store.ChangeTodo("add", title: title + "a"));
        store.ChangeTodo("toggle", first.Id);
        for (var i = 1; i < 100; i++)
            store.ChangeTodo("add", title: i.ToString());
        Assert.Throws<AgentException>(() => store.ChangeTodo("add", title: "101"));
        Assert.Equal(100, store.Todos().Items.Count);
    }

    [Fact]
    public void DedupeIsAtomicAcrossLinksAndRejectsChangedPayload()
    {
        using var store = new AgentStore(":memory:");
        var id = Wire.NewId();
        var executed = 0;
        Parallel.For(
            0,
            20,
            _ =>
            {
                if (store.BeginRequest("phone", id, "same").Execute)
                    Interlocked.Increment(ref executed);
            }
        );
        Assert.Equal(1, executed);
        Assert.Throws<AgentException>(() => store.BeginRequest("phone", id, "different"));
        store.CompleteRequest("phone", id, "succeeded", "{\"ok\":true}");
        Assert.Equal("{\"ok\":true}", store.BeginRequest("phone", id, "same").Result);
    }

    [Fact]
    public void RestartKeepsTodosAndMarksUnfinishedCommandsUnknown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mc-tests-" + Wire.NewId());
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "data.db");
        try
        {
            string id;
            using (var store = new AgentStore(file))
            {
                id = store.ChangeTodo("add", title: "持久化").Items[0].Id;
                store.BeginRequest("p", "r", "h");
            }
            using var reopened = new AgentStore(file);
            Assert.Equal(id, reopened.Todos().Items[0].Id);
            Assert.Equal("unknown", reopened.BeginRequest("p", "r", "h").State);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    internal static JsonElement Auth(
        ECDsa key,
        string host,
        string link,
        string nonce,
        PairingSession? session = null
    )
    {
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var id = Wire.Fingerprint(publicKey);
        var challenge = Encoding.UTF8.GetBytes(Wire.ChallengeText(host, link, nonce, id));
        return JsonSerializer.SerializeToElement(
            new
            {
                type = "authenticate",
                deviceId = id,
                publicKey = Convert.ToBase64String(publicKey),
                signature = Convert.ToBase64String(
                    key.SignData(
                        challenge,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence
                    )
                ),
                deviceName = "测试手机",
                pairSessionId = session?.Id,
                pairProof = session == null
                    ? null
                    : Convert.ToBase64String(HMACSHA256.HashData(session.Secret, challenge)),
            },
            Wire.Json
        );
    }

    [Fact]
    public async Task PairingNeedsFreshProofAndPcApprovalThenAuthenticatesBothLinks()
    {
        using var store = new AgentStore(":memory:");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = new PairingCoordinator(store, "host");
        var session = pairing.Begin();
        var approvals = 0;
        pairing.ApproveAsync = (request, _) =>
        {
            approvals++;
            Assert.Equal(6, request.ShortCode.Length);
            return Task.FromResult<string[]?>(PermissionSet.Basic);
        };
        var device = await pairing.AuthenticateAsync(
            Auth(key, "host", "usb", "nonce", session),
            "usb",
            "nonce",
            true,
            default
        );
        Assert.Equal(1, approvals);
        Assert.False(pairing.Active);
        Assert.Equal(PermissionSet.Basic, device.Permissions);
        var second = await pairing.AuthenticateAsync(
            Auth(key, "host", "wifi", "other"),
            "wifi",
            "other",
            false,
            default
        );
        Assert.Equal(device.DeviceId, second.DeviceId);
        Assert.Equal(1, approvals);
        await Assert.ThrowsAsync<AgentException>(
            () =>
                pairing.AuthenticateAsync(
                    Auth(key, "host", "usb", "nonce"),
                    "wifi",
                    "other",
                    false,
                    default
                )
        );
        store.SaveDevice(device with { Revoked = true });
        await Assert.ThrowsAsync<AgentException>(
            () =>
                pairing.AuthenticateAsync(
                    Auth(key, "host", "usb", "nonce"),
                    "usb",
                    "nonce",
                    false,
                    default
                )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePhoneCanPairAgainWithFreshApproval(bool revoked)
    {
        using var store = new AgentStore(":memory:");
        using var phone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherPhone = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = phone.ExportSubjectPublicKeyInfo();
        var id = Wire.Fingerprint(key);
        store.SaveDevice(new(id, "旧记录", Convert.ToBase64String(key), [], revoked));
        var pairing = new PairingCoordinator(store, "host");
        var approvals = 0;
        pairing.ApproveAsync = (request, _) =>
        {
            Assert.Equal(id, request.DeviceId);
            approvals++;
            return Task.FromResult<string[]?>(PermissionSet.Basic);
        };
        Task<TrustedDevice> Authenticate(ECDsa signer, PairingSession? code) =>
            pairing.AuthenticateAsync(Auth(signer, "host", "link", "nonce", code),
                "link", "nonce", true, default);
        if (revoked)
        {
            var rejected = await Assert.ThrowsAsync<AgentException>(() => Authenticate(phone, null));
            Assert.Equal("DEVICE_REVOKED", rejected.Code);
        }
        var expired = pairing.Begin();
        var fresh = pairing.Begin();
        await Assert.ThrowsAsync<AgentException>(() => Authenticate(phone, expired));
        if (!revoked)
        {
            var busy = await Assert.ThrowsAsync<AgentException>(() => Authenticate(otherPhone, fresh));
            Assert.Equal("DEVICE_BUSY", busy.Code);
        }
        Assert.Equal(0, approvals);
        var device = await Authenticate(phone, fresh);
        Assert.Equal(1, approvals);
        Assert.False(device.Revoked);
        Assert.Equal(PermissionSet.Basic, store.Device()!.Permissions);
        Assert.False(pairing.Active);
    }

    [Fact]
    public async Task CancelledPairingCannotBeApprovedLate()
    {
        using var store = new AgentStore(":memory:");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = new PairingCoordinator(store, "h");
        var session = pairing.Begin();
        pairing.ApproveAsync = (_, _) =>
        {
            pairing.Cancel();
            return Task.FromResult<string[]?>(PermissionSet.All);
        };
        await Assert.ThrowsAsync<AgentException>(
            () =>
                pairing.AuthenticateAsync(
                    Auth(key, "h", "l", "n", session),
                    "l",
                    "n",
                    true,
                    default
                )
        );
        Assert.Null(store.Device());
    }

    [Fact]
    public async Task UnknownPhoneCannotOpenMediaOrUseAnotherQrSecret()
    {
        using var store = new AgentStore(":memory:");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = new PairingCoordinator(store, "h");
        var old = pairing.Begin();
        pairing.Begin();
        await Assert.ThrowsAsync<AgentException>(
            () => pairing.AuthenticateAsync(Auth(key, "h", "l", "n", old), "l", "n", true, default)
        );
        await Assert.ThrowsAsync<AgentException>(
            () => pairing.AuthenticateAsync(Auth(key, "h", "l", "n"), "l", "n", false, default)
        );
        Assert.Null(store.Device());
    }
}
