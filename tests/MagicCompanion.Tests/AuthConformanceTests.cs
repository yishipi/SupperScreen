using System.Security.Cryptography;
using System.Text;
using MagicCompanion.Core;
using Xunit;

namespace MagicCompanion.Tests;

public sealed class AuthConformanceTests
{
    [Fact]
    public void SharedSignatureMatchesActualServerTranscript()
    {
        var vector = File.ReadAllLines(
                Path.Combine(AppContext.BaseDirectory, "auth-vector.properties")
            )
            .Where(x => x.Contains('='))
            .ToDictionary(x => x[..x.IndexOf('=')], x => x[(x.IndexOf('=') + 1)..]);
        var publicKey = Convert.FromBase64String(vector["publicKey"]);
        Assert.Equal(vector["deviceId"], Wire.Fingerprint(publicKey));
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);
        var transcript = Encoding.UTF8.GetBytes(
            Wire.ChallengeText(
                vector["hostId"],
                vector["linkId"],
                vector["nonce"],
                vector["deviceId"]
            )
        );
        Assert.True(
            key.VerifyData(
                transcript,
                Convert.FromBase64String(vector["signature"]),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence
            )
        );
    }
}
