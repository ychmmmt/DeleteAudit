using DeleteAudit.Domain;
using DeleteAudit.Infrastructure;
using DeleteAudit.Infrastructure.Importing.Sources;

namespace DeleteAudit.UnitTests.Importing.Sources;

public sealed class OfflineInputFileValidatorTests
{
    /// <summary>
    /// Only ever used as request text: every case in this file returns before the
    /// validator would create or write anything, so this directory is not created.
    /// The fictional server names below are never contacted.
    /// </summary>
    private static readonly string OutputDirectory =
        Path.Combine(RepositoryRoot.ArtifactsDirectory, "test-output");


    [Theory]
    [InlineData(FileAttributes.Directory)]
    [InlineData(FileAttributes.Device)]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Normal | FileAttributes.ReparsePoint)]
    public void HasUnsafeFileAttributesRejectsNonRegularAttributes(
        FileAttributes attributes)
    {
        Assert.True(OfflineInputFileValidator.HasUnsafeFileAttributes(attributes));
    }

    [Theory]
    [InlineData(FileAttributes.Normal)]
    [InlineData(FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.Archive)]
    public void HasUnsafeFileAttributesAllowsRegularFileAttributes(
        FileAttributes attributes)
    {
        Assert.False(OfflineInputFileValidator.HasUnsafeFileAttributes(attributes));
    }

    [Theory]
    [InlineData(@"\\fixture-server\share\selected.xml")]
    [InlineData("//fixture-server/share/selected.xml")]
    public async Task UnconfirmedNetworkPathIsRejectedBeforeAnyFilesystemAccess(
        string inputPath)
    {
        // The request carries the default NetworkPathConfirmed = false. If the
        // validator did not stop here it would call File.GetAttributes on the
        // share, which is exactly the network access under test — so reaching a
        // clean diagnostic instead of an I/O outcome is the proof.
        var request = OfflineSourceTestSupport.Request(inputPath, OutputDirectory);

        var result = await OfflineInputFileValidator.TryOpenAsync(request, ".xml");

        Assert.False(result.IsSuccess);
        Assert.Null(result.File);
        Assert.Equal(
            "network_path_confirmation_required",
            Assert.IsType<ImportDiagnostic>(result.Diagnostic).Code);
    }

    [Theory]
    [InlineData(@"\\?\C:\logs\selected.xml")]
    [InlineData(@"\\.\C:\logs\selected.xml")]
    [InlineData(@"\??\C:\logs\selected.xml")]
    [InlineData(@"\\?\UNC\fixture-server\share\selected.xml")]
    [InlineData(@"\\?\unc\fixture-server\share\selected.xml")]
    public async Task DeviceNamespacePathIsRejectedOutrightAndIsNotConfirmable(
        string inputPath)
    {
        // Confirmed on purpose: a device path must be refused even when the caller
        // claims authorisation, and \\?\UNC\… must never surface as a confirmable
        // share.
        var request = OfflineSourceTestSupport.Request(inputPath, OutputDirectory)
            with
            {
                NetworkPathConfirmed = true
            };

        var result = await OfflineInputFileValidator.TryOpenAsync(request, ".xml");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "device_path_rejected",
            Assert.IsType<ImportDiagnostic>(result.Diagnostic).Code);
    }

    [Theory]
    [InlineData(@"\\?\C:\logs\selected.xml")]
    [InlineData(@"\\.\C:\logs\selected.xml")]
    [InlineData(@"\??\C:\logs\selected.xml")]
    [InlineData(@"\\?\UNC\fixture-server\share\selected.xml")]
    public async Task DeviceNamespacePathNeverSurfacesAsAConfirmableShare(
        string inputPath)
    {
        var request = OfflineSourceTestSupport.Request(inputPath, OutputDirectory);

        var result = await OfflineInputFileValidator.TryOpenAsync(request, ".xml");

        Assert.NotEqual(
            "network_path_confirmation_required",
            Assert.IsType<ImportDiagnostic>(result.Diagnostic).Code);
    }

    [Fact]
    public async Task RelativePathIsStillRejectedByTheExistingRule()
    {
        var request = OfflineSourceTestSupport.Request(
            Path.Combine("logs", "selected.xml"),
            OutputDirectory);

        var result = await OfflineInputFileValidator.TryOpenAsync(request, ".xml");

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "invalid_import_request",
            Assert.IsType<ImportDiagnostic>(result.Diagnostic).Code);
    }
}
