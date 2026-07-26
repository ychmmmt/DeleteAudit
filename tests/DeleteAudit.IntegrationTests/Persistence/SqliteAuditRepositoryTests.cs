using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.Integrity;
using DeleteAudit.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.IntegrationTests.Persistence;

public sealed class SqliteAuditRepositoryTests
{
    [Fact]
    public async Task WriteRawEventsDuplicateSourceEventIsIdempotent()
    {
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteAuditRepository(connection);
        await repository.ValidateSchemaAsync();
        var item = CreatePersisted(1);

        var first = await repository.WriteRawEventsAsync([item]);
        var second = await repository.WriteRawEventsAsync([item]);
        var rows = await repository.ReadRawEventsAsync(10);

        Assert.Equal(1, first.InsertedCount);
        Assert.Equal(0, second.InsertedCount);
        Assert.Single(rows);
        Assert.Equal(item.Event.RawEventId, rows[0].RawEventId);
    }

    [Fact]
    public async Task WriteRawEventsConstraintFailureRollsBackEntireTransaction()
    {
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteAuditRepository(connection);
        var baseline = CreatePersisted(1);
        await repository.WriteRawEventsAsync([baseline]);

        var firstInBatch = CreatePersisted(2);
        var conflicting = CreatePersisted(3) with { ChainEntry = firstInBatch.ChainEntry };

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.WriteRawEventsAsync([firstInBatch, conflicting]));

        var rows = await repository.ReadRawEventsAsync(10);
        Assert.Single(rows);
        Assert.Equal(baseline.Event.RawEventId, rows[0].RawEventId);
    }

    [Fact]
    public async Task ReadRawEventsRejectsUnboundedLimit()
    {
        await using var connection = await CreateDatabaseAsync();
        using var repository = new SqliteAuditRepository(connection);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ReadRawEventsAsync(1001));
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema.sql"));
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static PersistedRawEvent CreatePersisted(int sequence)
    {
        var occurred = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)
            + TimeSpan.FromSeconds(sequence);
        var raw = new RawWindowsEvent(
            $"raw-{sequence}",
            WindowsEventSource.SysmonDelete,
            "LAB-PC",
            "Microsoft-Windows-Sysmon/Operational",
            "Microsoft-Windows-Sysmon",
            26,
            1000 + sequence,
            occurred,
            occurred + TimeSpan.FromMilliseconds(50),
            $"<Event fixture=\"{sequence}\" />",
            new Dictionary<string, string?>(),
            []);
        var chain = JsonlHashChain.CreateEntry(new { raw.RawEventId, raw.EventId });
        return new PersistedRawEvent(
            raw,
            "epoch-lab",
            sequence,
            occurred.ToOffset(TimeSpan.FromHours(1)),
            "GMT Standard Time",
            chain);
    }
}
