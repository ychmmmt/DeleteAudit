using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;
using DeleteAudit.Infrastructure.Importing.Output;
using DeleteAudit.Infrastructure.Integrity;

namespace DeleteAudit.UnitTests.Importing.Output;

public sealed class FileImportJsonlWriterTests
{
    private static readonly string TestOutputRoot =
        Path.Combine(RepositoryRoot.ArtifactsDirectory, "test-output");

    [Fact]
    public async Task WriteAsyncCreatesDeterministicHashChainedJsonlAndSuccessManifest()
    {
        var outputDirectory = CreateUniqueOutputDirectory();
        var session = CreateSession(outputDirectory);
        var writer = new FileImportJsonlWriter();

        var result = await writer.WriteAsync(
            session,
            CreateOutOfOrderRecords(),
            outputDirectory);

        Assert.True(result.Success);
        Assert.Null(result.Diagnostic);
        var jsonlPath = Assert.IsType<string>(result.JsonlPath);
        var manifestPath = Assert.IsType<string>(result.ManifestPath);
        Assert.Equal(3, result.EntryCount);
        Assert.True(File.Exists(jsonlPath));
        Assert.True(File.Exists(manifestPath));

        var jsonlBytes = await File.ReadAllBytesAsync(jsonlPath);
        Assert.False(jsonlBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.DoesNotContain((byte)'\r', jsonlBytes);
        Assert.Equal(3, jsonlBytes.Count(value => value == (byte)'\n'));
        Assert.Equal((byte)'\n', jsonlBytes[^1]);

        var lines = await File.ReadAllLinesAsync(jsonlPath);
        Assert.Equal(3, lines.Length);
        var verification = JsonlHashChain.Verify(lines);
        Assert.True(verification.IsValid);
        Assert.Equal(3, verification.VerifiedEntryCount);
        Assert.Equal(result.LastHash, verification.LastEntryHash);

        var recordNumbers = lines
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement
                    .GetProperty("payload")
                    .GetProperty("recordNumber")
                    .GetInt64();
            })
            .ToArray();
        Assert.Equal(new long[] { 1, 2, 3 }, recordNumbers);

        using var firstLine = JsonDocument.Parse(lines[0]);
        var firstEntryHash = firstLine.RootElement
            .GetProperty("entryHash")
            .GetString();
        Assert.Equal(result.FirstHash, firstEntryHash);

        var calculatedFileHash = Convert
            .ToHexString(SHA256.HashData(jsonlBytes))
            .ToLowerInvariant();
        Assert.Equal(calculatedFileHash, result.JsonlSha256);

        await using var manifestStream = File.OpenRead(manifestPath);
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        var root = manifest.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal(session.ImportSessionId, root.GetProperty("importSessionId").GetString());
        Assert.Equal(3, root.GetProperty("entryCount").GetInt32());
        Assert.Equal(result.FirstHash, root.GetProperty("firstEntryHash").GetString());
        Assert.Equal(result.LastHash, root.GetProperty("lastEntryHash").GetString());
        Assert.Equal(calculatedFileHash, root.GetProperty("jsonlSha256").GetString());
        Assert.Equal(
            Path.GetFileName(jsonlPath),
            root.GetProperty("jsonlFileName").GetString());
        Assert.False(File.Exists($"{manifestPath}.pending"));
    }

    [Fact]
    public async Task VerifyDetectsTamperedJsonlRecord()
    {
        var outputDirectory = CreateUniqueOutputDirectory();
        var session = CreateSession(outputDirectory);
        var writer = new FileImportJsonlWriter();
        var result = await writer.WriteAsync(
            session,
            CreateOutOfOrderRecords(),
            outputDirectory);
        Assert.True(result.Success);

        var original = await File.ReadAllTextAsync(result.JsonlPath!);
        var tampered = original.Replace(
            "\"raw-1\"",
            "\"tampered-1\"",
            StringComparison.Ordinal);
        Assert.NotEqual(original, tampered);
        await File.WriteAllTextAsync(
            result.JsonlPath!,
            tampered,
            new UTF8Encoding(false));

        var verification = JsonlHashChain.Verify(File.ReadLines(result.JsonlPath!));

        Assert.False(verification.IsValid);
        Assert.Equal(0, verification.FailedEntryIndex);
        Assert.Equal("content_hash_mismatch", verification.FailureReason);
    }

    [Fact]
    public async Task WriteAsyncSameSessionFailsWithoutOverwritingSuccessfulFiles()
    {
        var outputDirectory = CreateUniqueOutputDirectory();
        var session = CreateSession(outputDirectory);
        var writer = new FileImportJsonlWriter();
        var first = await writer.WriteAsync(
            session,
            CreateOutOfOrderRecords(),
            outputDirectory);
        Assert.True(first.Success);
        var originalJsonl = await File.ReadAllBytesAsync(first.JsonlPath!);
        var originalManifest = await File.ReadAllBytesAsync(first.ManifestPath!);

        var second = await writer.WriteAsync(
            session,
            CreateOutOfOrderRecords(),
            outputDirectory);

        Assert.False(second.Success);
        Assert.Equal("jsonl_write_failed", second.Diagnostic!.Code);
        Assert.Null(second.ManifestPath);
        Assert.Equal(originalJsonl, await File.ReadAllBytesAsync(first.JsonlPath!));
        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(first.ManifestPath!));
        Assert.False(File.Exists($"{first.ManifestPath}.pending"));
    }

    private static string CreateUniqueOutputDirectory()
    {
        var path = Path.Combine(
            TestOutputRoot,
            $"jsonl-writer-{Guid.NewGuid():D}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ImportSession CreateSession(string outputDirectory)
    {
        var startedUtc = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        return new ImportSession(
            Guid.NewGuid().ToString("D"),
            "events.xml",
            Path.Combine(outputDirectory, "events.xml"),
            512,
            startedUtc - TimeSpan.FromMinutes(1),
            new string('a', 64),
            startedUtc,
            startedUtc + TimeSpan.FromSeconds(1),
            3,
            1,
            1,
            1,
            "1.1.0-test",
            2,
            ImportStatus.Completed);
    }

    private static ImportJsonlRecord[] CreateOutOfOrderRecords() =>
    [
        new ImportJsonlRecord(
            3,
            ImportRecordOutcome.Error,
            null,
            null,
            "<Event malformed=\"true\">",
            [
                new ImportDiagnostic(
                    "malformed_xml",
                    "Synthetic malformed XML.",
                    ImportDiagnosticSeverity.Error,
                    "parse",
                    3)
            ]),
        new ImportJsonlRecord(
            1,
            ImportRecordOutcome.Succeeded,
            26,
            "raw-1",
            "<Event id=\"1\" />",
            []),
        new ImportJsonlRecord(
            2,
            ImportRecordOutcome.Ignored,
            4663,
            "raw-2",
            "<Event id=\"2\" />",
            [
                new ImportDiagnostic(
                    "non_delete_access",
                    "Synthetic non-delete access.",
                    ImportDiagnosticSeverity.Information,
                    "parse",
                    2)
            ])
    ];
}
