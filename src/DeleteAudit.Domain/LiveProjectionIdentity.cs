using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DeleteAudit.Domain;

/// <summary>
/// Identity and continuity rules for the live-owned canonical projection.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is derived from evidence that already exists, so projecting the same
/// captured record twice can only ever produce the same identity. Nothing borrows an
/// offline identity: there is no import session, no input-file digest, no offline channel
/// epoch and no offline hash-chain anchor involved.
/// </para>
/// <para>
/// The continuity hash orders one capture session's projection and makes an accidental
/// modification visible. It is <b>not</b> a tamper-proofing guarantee — anyone who can
/// write to the database can recompute a whole chain — and must never be described as one.
/// </para>
/// </remarks>
public static class LiveProjectionIdentity
{
    /// <summary>
    /// The anchor a session's first projected record chains from. It is an explicit
    /// all-zero value meaning "start of this capture session's chain", and it is
    /// deliberately not linked to any other chain.
    /// </summary>
    public static ReadOnlySpan<byte> ChainAnchor => ChainAnchorBytes;

    private static readonly byte[] ChainAnchorBytes = new byte[32];

    /// <summary>Derived from the projected evidence, so a replay collides with itself.</summary>
    public static string CreateProjectionId(string liveEvidenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveEvidenceId);
        return string.Concat("live-projection:", liveEvidenceId);
    }

    /// <summary>
    /// Identifies one capture session's span on one channel of one machine. A missing
    /// machine name is represented explicitly rather than collapsed into an empty string,
    /// so "not reported" and "reported as empty" cannot become the same epoch.
    /// </summary>
    public static string CreateEpochId(
        string liveSessionId,
        string channelName,
        string? machineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        var canonical = EncodeFields(
            "live-channel-epoch-v1",
            liveSessionId,
            channelName,
            machineName);
        return string.Concat(
            "live-epoch:",
            Convert.ToHexString(SHA256.HashData(canonical))
                .ToUpperInvariant());
    }

    /// <summary>
    /// Digests every canonical projection field. Length-prefixed encoding is used because
    /// event data is untrusted and may itself contain control characters or separators.
    /// A null value and an empty value remain different.
    /// </summary>
    public static byte[] ComputePayloadHash(LiveProjectionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.ChannelName);

        var fields = new List<string?>
        {
            "live-projection-payload-v1",
            payload.Source,
            Number(payload.EventRecordId),
            payload.ProviderName,
            payload.ChannelName,
            payload.MachineName,
            Time(payload.EventUtc),
            Time(payload.ObservedUtc),
            payload.ParserRawEventId,
            Number(payload.ParsedEventId),
            payload.NormalizedPath,
            payload.ObjectKind,
            Number(payload.ProcessId),
            payload.ProcessPath,
            payload.ProcessGuid,
            payload.CommandLine,
            Number(payload.ParentProcessId),
            payload.ParentProcessPath,
            payload.ParentProcessGuid,
            payload.UserName,
            payload.UserSid,
            payload.DeletePermission,
            Boolean(payload.ArchiveExpected)
        };
        fields.AddRange(
            payload.MissingFields
                .OrderBy(field => field, StringComparer.Ordinal)
                .Select(field => $"missing:{field}"));
        return SHA256.HashData(EncodeFields([.. fields]));
    }

    /// <summary>
    /// Computes one record's continuity hash. The canonical input is fixed and ordered:
    /// <code>
    /// previous-hash-hex ␟ live-evidence-id ␟ epoch-id ␟ ingest-sequence ␟ raw-xml-sha256-hex
    /// </code>
    /// with the previous hash rendered as the all-zero anchor for a session's first
    /// record. Every component is already a stored fact, so the hash is reproducible from
    /// the database alone.
    /// </summary>
    public static byte[] ComputeEntryHash(
        ReadOnlySpan<byte> previousEntryHash,
        string liveEvidenceId,
        string liveChannelEpochId,
        long liveIngestSequence,
        ReadOnlySpan<byte> rawXmlSha256,
        ReadOnlySpan<byte> canonicalPayloadSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveEvidenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveChannelEpochId);
        if (liveIngestSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(liveIngestSequence),
                liveIngestSequence,
                "A live ingest sequence starts at 1 and increases strictly.");
        }

        if (previousEntryHash.Length != 32)
        {
            throw new ArgumentException(
                "A previous entry hash must be 32 bytes; use ChainAnchor for the first record.",
                nameof(previousEntryHash));
        }

        if (rawXmlSha256.Length != 32)
        {
            throw new ArgumentException(
                "A raw XML digest must be 32 bytes.",
                nameof(rawXmlSha256));
        }

        if (canonicalPayloadSha256.Length != 32)
        {
            throw new ArgumentException(
                "A canonical payload digest must be 32 bytes.",
                nameof(canonicalPayloadSha256));
        }

        var canonical = EncodeFields(
            "live-projection-entry-v1",
            Convert.ToHexString(previousEntryHash).ToUpperInvariant(),
            liveEvidenceId,
            liveChannelEpochId,
            liveIngestSequence.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(rawXmlSha256).ToUpperInvariant(),
            Convert.ToHexString(canonicalPayloadSha256).ToUpperInvariant());
        return SHA256.HashData(canonical);
    }

    /// <summary>
    /// Maps a capture outcome onto the projection's source vocabulary. Only the three
    /// classified outcomes are projectable; ignored and error records establish nothing
    /// and are deliberately left out of the canonical projection.
    /// </summary>
    public static bool TryGetSource(LiveEventOutcome outcome, out string source)
    {
        source = outcome switch
        {
            LiveEventOutcome.DeleteFact => "sysmon_delete",
            LiveEventOutcome.ProcessContext => "sysmon_process",
            LiveEventOutcome.SecurityEvidence => "security_4663",
            _ => string.Empty
        };
        return source.Length != 0;
    }

    private static string? Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string? Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string? Boolean(bool? value) =>
        value is null ? null : value.Value ? "true" : "false";

    private static string? Time(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Encodes fields as a versioned series of signed 32-bit big-endian byte lengths
    /// followed by UTF-8 bytes. A length of -1 represents null.
    /// </summary>
    private static byte[] EncodeFields(params string?[] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            if (field is null)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, -1);
                stream.Write(length);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(field);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }
}

/// <summary>
/// The canonical values derived from one projectable live evidence row. Nullable fields
/// remain null when the source did not report them; projection never fills a gap by
/// inference.
/// </summary>
public sealed record LiveProjectionPayload(
    string Source,
    long? EventRecordId,
    string? ProviderName,
    string ChannelName,
    string? MachineName,
    DateTimeOffset? EventUtc,
    DateTimeOffset ObservedUtc,
    string? ParserRawEventId,
    int? ParsedEventId,
    string? NormalizedPath,
    string? ObjectKind,
    int? ProcessId,
    string? ProcessPath,
    string? ProcessGuid,
    string? CommandLine,
    int? ParentProcessId,
    string? ParentProcessPath,
    string? ParentProcessGuid,
    string? UserName,
    string? UserSid,
    string? DeletePermission,
    bool? ArchiveExpected,
    IReadOnlyList<string> MissingFields);
