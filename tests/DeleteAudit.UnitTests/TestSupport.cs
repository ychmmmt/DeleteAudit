using DeleteAudit.Domain;

namespace DeleteAudit.UnitTests;

internal static class TestSupport
{
    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static NormalizedDeleteEvent DeleteEvent(
        int sequence,
        DateTimeOffset occurredUtc,
        string? processGuid = "{11111111-1111-1111-1111-111111111111}",
        int? processId = 4242,
        string? userName = "LAB\\Analyst",
        string? userSid = null,
        string? fullPath = @"C:\Lab\Batch\item.txt",
        string? processPath = @"C:\Lab\Tools\cleaner.exe") =>
        new(
            $"delete-{sequence}",
            $"raw-{sequence}",
            "LAB-PC",
            26,
            1000 + sequence,
            occurredUtc,
            fullPath,
            AuditObjectKind.Unknown,
            processId,
            processPath,
            processGuid,
            null,
            null,
            null,
            null,
            userName,
            userSid,
            DeletePermissionType.NotObserved,
            false,
            []);

    public static ProcessContextEvent ProcessEvent(
        string rawId,
        DateTimeOffset startedUtc,
        int? processId = 4242,
        string? processGuid = "{11111111-1111-1111-1111-111111111111}",
        string? userName = "LAB\\Analyst",
        string? userSid = null,
        string? processPath = @"C:\Lab\Tools\cleaner.exe",
        string? commandLine = "cleaner.exe --fixture") =>
        new(
            rawId,
            "LAB-PC",
            900,
            startedUtc,
            processId,
            processGuid,
            processPath,
            commandLine,
            2000,
            @"C:\Windows\explorer.exe",
            "{22222222-2222-2222-2222-222222222222}",
            userName,
            userSid,
            []);
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}
