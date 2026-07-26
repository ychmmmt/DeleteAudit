using DeleteAudit.Infrastructure.Importing.Sources;

namespace DeleteAudit.UnitTests.Importing.Sources;

public sealed class OfflineInputFileValidatorTests
{
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
}
