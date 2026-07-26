using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;

namespace DeleteAudit.UnitTests.Importing;

/// <summary>
/// Every case here is a string. Nothing in this file opens, probes, enumerates or
/// resolves a path, and no host named below is ever contacted — the server and
/// share names are fictional placeholders that exist only as text.
/// </summary>
public sealed class InputPathClassifierTests
{
    [Theory]
    [InlineData('C')]
    [InlineData('D')]
    [InlineData('E')]
    [InlineData('Z')]
    public void FullyQualifiedLocalPathIsNeverClassifiedByItsDriveLetter(char driveLetter)
    {
        // Z: is included on purpose: a mapped network drive is textually a local
        // path and stays on the normal flow. No letter is treated specially.
        var path = string.Concat(
            driveLetter,
            Path.VolumeSeparatorChar,
            Path.DirectorySeparatorChar,
            "logs",
            Path.DirectorySeparatorChar,
            "selected.xml");

        Assert.Equal(InputPathKind.LocalFullyQualified, InputPathClassifier.Classify(path));
    }

    [Theory]
    [InlineData(@"\\fixture-server\share\selected.evtx")]
    [InlineData(@"\\fixture-server\share\selected.xml")]
    [InlineData("//fixture-server/share/selected.xml")]
    [InlineData("//fixture-server/share/selected.evtx")]
    [InlineData(@"\\fixture-server/share\selected.xml")]
    public void PlainUncPathIsClassifiedAsNetworkShare(string path)
    {
        Assert.Equal(InputPathKind.NetworkShare, InputPathClassifier.Classify(path));
    }

    [Theory]
    [InlineData(@"\\?\C:\logs\selected.xml")]
    [InlineData(@"\\.\C:\logs\selected.xml")]
    [InlineData(@"\??\C:\logs\selected.xml")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("//?/C:/logs/selected.xml")]
    [InlineData("//./C:/logs/selected.xml")]
    public void DeviceNamespacePathIsClassifiedAsDevice(string path)
    {
        Assert.Equal(InputPathKind.DeviceNamespace, InputPathClassifier.Classify(path));
    }

    [Theory]
    [InlineData(@"\\?\UNC\fixture-server\share\selected.xml")]
    [InlineData(@"\\?\unc\fixture-server\share\selected.evtx")]
    public void DeviceUncPrefixIsDeviceAndNeverAConfirmableShare(string path)
    {
        var kind = InputPathClassifier.Classify(path);

        Assert.Equal(InputPathKind.DeviceNamespace, kind);
        // The point of the priority order: this must never reach the prompt.
        Assert.NotEqual(InputPathKind.NetworkShare, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"logs\selected.xml")]
    [InlineData("logs/selected.xml")]
    [InlineData(@"\logs\selected.xml")]
    [InlineData("C:selected.xml")]
    public void OtherPathsKeepExistingInvalidPathHandling(string? path)
    {
        Assert.Equal(InputPathKind.Other, InputPathClassifier.Classify(path));
    }

    [Fact]
    public void ClassificationPerformsNoInputOutput()
    {
        // Structural guard: the whole design depends on classification staying a
        // pure string decision, because it runs before the confirmation prompt.
        // Inspecting the shipped source is the only way to assert that without
        // reimplementing it here.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Value,
            "src",
            "DeleteAudit.Domain",
            "InputPathClassification.cs"));

        foreach (var forbidden in new[]
                 {
                     "File.",
                     "Directory.",
                     "DriveInfo",
                     "GetLogicalDrives",
                     "EnumerateFiles",
                     "EnumerateDirectories",
                     "GetDirectories",
                     "FileStream",
                     "Path.GetFullPath",
                     "Exists"
                 })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClassificationHasNoHardCodedDriveLetter()
    {
        // No volume may be singled out by its letter, which is also why a mapped
        // network drive cannot be detected here.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Value,
            "src",
            "DeleteAudit.Domain",
            "InputPathClassification.cs"));

        Assert.DoesNotContain("VolumeSeparatorChar", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(
                @"""[A-Za-z]:",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(2)),
            source);
    }
}
