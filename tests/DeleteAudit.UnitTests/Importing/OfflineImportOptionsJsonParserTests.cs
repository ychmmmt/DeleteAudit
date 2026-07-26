using DeleteAudit.Infrastructure.Importing;

namespace DeleteAudit.UnitTests.Importing;

public sealed class OfflineImportOptionsJsonParserTests
{
    [Fact]
    public void ParseReadsConfiguredFileSizeAndOutputBoundary()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "appsettings.example.json"));

        var options = OfflineImportOptionsJsonParser.Parse(json);

        Assert.Equal(67_108_864, options.MaximumFileSizeBytes);
        // The shipped example uses a generic absolute directory rather than any
        // developer machine path; callers substitute their own.
        Assert.Equal(
            @"C:\DeleteAudit\artifacts\test-output",
            options.JsonlOutputDirectory);
        Assert.Equal(2, options.SchemaVersion);
    }
}
