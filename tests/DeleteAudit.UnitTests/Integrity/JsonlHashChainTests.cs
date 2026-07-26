using System.Security.Cryptography;
using System.Text;
using DeleteAudit.Infrastructure.Integrity;

namespace DeleteAudit.UnitTests.Integrity;

public sealed class JsonlHashChainTests
{
    [Fact]
    public void VerifyValidChainSucceeds()
    {
        var first = JsonlHashChain.CreateEntry(new { name = "one", sequence = 1 });
        var second = JsonlHashChain.CreateEntry(
            new { name = "two", sequence = 2 },
            first.EntryHash);

        var result = JsonlHashChain.Verify([first.JsonLine, second.JsonLine]);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.VerifiedEntryCount);
        Assert.Equal(second.EntryHash, result.LastEntryHash);
    }

    [Fact]
    public void VerifyModifiedPayloadDetectsTampering()
    {
        var entry = JsonlHashChain.CreateEntry(new { name = "one", sequence = 1 });
        var tampered = entry.JsonLine.Replace("\"one\"", "\"tampered\"", StringComparison.Ordinal);

        var result = JsonlHashChain.Verify([tampered]);

        Assert.False(result.IsValid);
        Assert.Equal("content_hash_mismatch", result.FailureReason);
    }

    [Fact]
    public void VerifyCrossDayEntryUsesPriorDayFinalHash()
    {
        var dayOne = JsonlHashChain.CreateEntry(new { day = 1 });
        var dayTwo = JsonlHashChain.CreateEntry(new { day = 2 }, dayOne.EntryHash);

        var result = JsonlHashChain.Verify([dayTwo.JsonLine], dayOne.EntryHash);

        Assert.True(result.IsValid);
        Assert.Equal(dayOne.EntryHash, dayTwo.PreviousEntryHash);
    }

    [Fact]
    public async Task TestSignerExplicitlyReportsNonHardwareSecurityClaim()
    {
        IIntegrityCheckpointSigner signer = new DeterministicTestSigner();
        var signature = await signer.SignAsync(new byte[32]);

        Assert.Equal("HMAC-SHA256-TEST-ONLY", signature.Algorithm);
        Assert.Equal("TEST_ONLY_NOT_HARDWARE_BACKED", signature.SecurityClaim);
        Assert.NotEmpty(signature.Signature);
    }

    private sealed class DeterministicTestSigner : IIntegrityCheckpointSigner
    {
        private static readonly byte[] TestKey = Encoding.UTF8.GetBytes(
            "DeleteAudit deterministic test key; not a production secret.");

        public ValueTask<IntegritySignature> SignAsync(
            ReadOnlyMemory<byte> checkpointHash,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hmac = new HMACSHA256(TestKey);
            return ValueTask.FromResult(
                new IntegritySignature(
                    "HMAC-SHA256-TEST-ONLY",
                    "deterministic-unit-test-key",
                    hmac.ComputeHash(checkpointHash.ToArray()),
                    "TEST_ONLY_NOT_HARDWARE_BACKED"));
        }
    }
}
