using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeleteAudit.Infrastructure.Integrity;

public static class JsonlHashChain
{
    public const int FormatVersion = 1;
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static HashChainEntry CreateEntry<T>(
        T payload,
        string? previousEntryHash = null)
    {
        var previous = NormalizePreviousHash(previousEntryHash);
        var payloadBytes = Canonicalize(JsonSerializer.SerializeToElement(payload));
        var contentHash = Hash(payloadBytes);
        var entryHash = ComputeEntryHash(previous, contentHash);
        var line = BuildLine(previous, contentHash, entryHash, payloadBytes);
        return new HashChainEntry(line, contentHash, previous, entryHash);
    }

    public static HashChainVerificationResult Verify(
        IEnumerable<string> jsonLines,
        string? initialPreviousEntryHash = null)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        var expectedPrevious = NormalizePreviousHash(initialPreviousEntryHash);
        var index = 0;

        foreach (var line in jsonLines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var formatVersion = root.GetProperty("formatVersion").GetInt32();
                var previous = root.GetProperty("previousEntryHash").GetString();
                var contentHash = root.GetProperty("contentHash").GetString();
                var entryHash = root.GetProperty("entryHash").GetString();
                var payload = root.GetProperty("payload");

                if (formatVersion != FormatVersion)
                {
                    return Failure(index, "unsupported_format_version", expectedPrevious);
                }

                if (!FixedEquals(previous, expectedPrevious))
                {
                    return Failure(index, "previous_hash_mismatch", expectedPrevious);
                }

                var actualContentHash = Hash(Canonicalize(payload));
                if (!FixedEquals(contentHash, actualContentHash))
                {
                    return Failure(index, "content_hash_mismatch", expectedPrevious);
                }

                var actualEntryHash = ComputeEntryHash(expectedPrevious, actualContentHash);
                if (!FixedEquals(entryHash, actualEntryHash))
                {
                    return Failure(index, "entry_hash_mismatch", expectedPrevious);
                }

                expectedPrevious = actualEntryHash;
                index++;
            }
            catch (JsonException)
            {
                return Failure(index, "invalid_json", expectedPrevious);
            }
            catch (InvalidOperationException)
            {
                return Failure(index, "missing_or_invalid_chain_field", expectedPrevious);
            }
            catch (FormatException)
            {
                return Failure(index, "invalid_hash_encoding", expectedPrevious);
            }
        }

        return new HashChainVerificationResult(
            true,
            index,
            null,
            null,
            index == 0 ? initialPreviousEntryHash : expectedPrevious);
    }

    private static HashChainVerificationResult Failure(
        int index,
        string reason,
        string lastHash) =>
        new(false, index, index, reason, index == 0 ? null : lastHash);

    private static string BuildLine(
        string previous,
        string contentHash,
        string entryHash,
        byte[] payloadBytes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("previousEntryHash", previous);
            writer.WriteString("contentHash", contentHash);
            writer.WriteString("entryHash", entryHash);
            writer.WritePropertyName("payload");
            using var payload = JsonDocument.Parse(payloadBytes);
            payload.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static byte[] Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }

        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON kind {element.ValueKind}.");
        }
    }

    private static string ComputeEntryHash(string previousHash, string contentHash)
    {
        var material = Encoding.UTF8.GetBytes($"{FormatVersion}\n{previousHash}\n{contentHash}");
        return Hash(material);
    }

    private static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string NormalizePreviousHash(string? value)
    {
        if (value is null)
        {
            return GenesisHash;
        }

        if (value.Length != 64)
        {
            throw new FormatException("A chain hash must contain 64 hexadecimal characters.");
        }

        _ = Convert.FromHexString(value);
        return value.ToLowerInvariant();
    }

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
