namespace DeleteAudit.Infrastructure.Integrity;

public sealed record IntegritySignature(
    string Algorithm,
    string KeyId,
    byte[] Signature,
    string SecurityClaim);

public interface IIntegrityCheckpointSigner
{
    ValueTask<IntegritySignature> SignAsync(
        ReadOnlyMemory<byte> checkpointHash,
        CancellationToken cancellationToken = default);
}
