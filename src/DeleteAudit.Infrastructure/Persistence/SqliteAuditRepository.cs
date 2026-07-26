using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeleteAudit.Domain;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Persistence;

public sealed class SqliteAuditRepository : IReadOnlyAuditRepository, IDisposable
{
    private static readonly string[] RequiredTables =
    [
        "channel_epochs",
        "raw_events",
        "process_observations",
        "delete_sessions",
        "delete_events",
        "event_evidence",
        "session_members",
        "risk_assessments",
        "integrity_checkpoints"
    ];

    private readonly SqliteConnection _connection;
    private readonly SqliteBusyRetryOptions _retryOptions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteAuditRepository(
        SqliteConnection connection,
        SqliteBusyRetryOptions? retryOptions = null,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _retryOptions = retryOptions ?? SqliteBusyRetryOptions.Default;
        _retryOptions.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ValidateSchemaAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table';
                """;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                present.Add(reader.GetString(0));
            }

            var missing = RequiredTables.Where(table => !present.Contains(table)).ToArray();
            if (missing.Length != 0)
            {
                throw new InvalidOperationException(
                    $"The existing DeleteAudit schema is incomplete: {string.Join(", ", missing)}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SqliteWriteResult> WriteRawEventsAsync(
        IReadOnlyCollection<PersistedRawEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return new SqliteWriteResult(0, 0);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            return await ExecuteWithBusyRetryAsync(
                () => WriteTransactionAsync(events, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RawEventSummary>> ReadRawEventsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT raw_event_id, source, event_id, event_record_id, event_utc, computer_name
                FROM raw_events
                ORDER BY ingest_sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            var results = new List<RawEventSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new RawEventSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt64(3),
                    DateTimeOffset.Parse(
                        reader.GetString(4),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<SqliteWriteResult> WriteTransactionAsync(
        IReadOnlyCollection<PersistedRawEvent> events,
        CancellationToken cancellationToken)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            var inserted = 0;
            foreach (var item in events)
            {
                ValidateForPersistence(item);
                await InsertEpochAsync(item, transaction, cancellationToken).ConfigureAwait(false);
                inserted += await InsertRawEventAsync(item, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteWriteResult(events.Count, inserted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task InsertEpochAsync(
        PersistedRawEvent item,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO channel_epochs (
                channel_epoch_id,
                computer_name,
                channel_name,
                provider_name,
                started_utc,
                first_record_id,
                start_reason,
                coverage_gap)
            VALUES (
                $epoch_id,
                $computer,
                $channel,
                $provider,
                $started_utc,
                $first_record_id,
                'initial',
                0)
            ON CONFLICT(channel_epoch_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$epoch_id", item.ChannelEpochId);
        command.Parameters.AddWithValue("$computer", item.Event.ComputerName!);
        command.Parameters.AddWithValue("$channel", item.Event.ChannelName!);
        command.Parameters.AddWithValue("$provider", item.Event.ProviderName!);
        command.Parameters.AddWithValue("$started_utc", Format(item.Event.EventTimeUtc));
        command.Parameters.AddWithValue("$first_record_id", item.Event.EventRecordId!.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> InsertRawEventAsync(
        PersistedRawEvent item,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using (var existing = _connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT 1
                FROM raw_events
                WHERE computer_name = $computer_name
                  AND channel_epoch_id = $channel_epoch_id
                  AND event_record_id = $event_record_id
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$computer_name", item.Event.ComputerName!);
            existing.Parameters.AddWithValue("$channel_epoch_id", item.ChannelEpochId);
            existing.Parameters.AddWithValue("$event_record_id", item.Event.EventRecordId!.Value);
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return 0;
            }
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_events (
                raw_event_id,
                channel_epoch_id,
                source,
                computer_name,
                channel_name,
                provider_name,
                event_id,
                event_record_id,
                event_utc,
                event_local,
                local_utc_offset_minutes,
                windows_time_zone_id,
                observed_utc,
                raw_xml,
                raw_xml_sha256,
                ingest_sequence,
                previous_entry_hash,
                entry_hash,
                format_version)
            VALUES (
                $raw_event_id,
                $channel_epoch_id,
                $source,
                $computer_name,
                $channel_name,
                $provider_name,
                $event_id,
                $event_record_id,
                $event_utc,
                $event_local,
                $offset_minutes,
                $time_zone_id,
                $observed_utc,
                $raw_xml,
                $raw_xml_sha256,
                $ingest_sequence,
                $previous_entry_hash,
                $entry_hash,
                1);
            """;

        command.Parameters.AddWithValue("$raw_event_id", item.Event.RawEventId);
        command.Parameters.AddWithValue("$channel_epoch_id", item.ChannelEpochId);
        command.Parameters.AddWithValue("$source", ToStorageSource(item.Event.Source));
        command.Parameters.AddWithValue("$computer_name", item.Event.ComputerName!);
        command.Parameters.AddWithValue("$channel_name", item.Event.ChannelName!);
        command.Parameters.AddWithValue("$provider_name", item.Event.ProviderName!);
        command.Parameters.AddWithValue("$event_id", item.Event.EventId);
        command.Parameters.AddWithValue("$event_record_id", item.Event.EventRecordId!.Value);
        command.Parameters.AddWithValue("$event_utc", Format(item.Event.EventTimeUtc));
        command.Parameters.AddWithValue("$event_local", item.EventLocal.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$offset_minutes",
            checked((int)item.EventLocal.Offset.TotalMinutes));
        command.Parameters.AddWithValue("$time_zone_id", item.WindowsTimeZoneId);
        command.Parameters.AddWithValue("$observed_utc", Format(item.Event.ObservedUtc));
        command.Parameters.AddWithValue("$raw_xml", item.Event.RawXml);
        command.Parameters.Add("$raw_xml_sha256", SqliteType.Blob).Value =
            SHA256.HashData(Encoding.UTF8.GetBytes(item.Event.RawXml));
        command.Parameters.AddWithValue("$ingest_sequence", item.IngestSequence);
        command.Parameters.Add("$previous_entry_hash", SqliteType.Blob).Value =
            Convert.FromHexString(item.ChainEntry.PreviousEntryHash);
        command.Parameters.Add("$entry_hash", SqliteType.Blob).Value =
            Convert.FromHexString(item.ChainEntry.EntryHash);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteWithBusyRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException exception)
                when (IsBusyOrLocked(exception) && attempt < _retryOptions.MaxAttempts)
            {
                await Task
                    .Delay(_retryOptions.Delay, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void EnsureOpen()
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The repository requires an already-open SQLite connection.");
        }
    }

    private static void ValidateForPersistence(PersistedRawEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.ChannelEpochId)
            || string.IsNullOrWhiteSpace(item.Event.ComputerName)
            || string.IsNullOrWhiteSpace(item.Event.ChannelName)
            || string.IsNullOrWhiteSpace(item.Event.ProviderName)
            || item.Event.EventRecordId is null
            || string.IsNullOrWhiteSpace(item.WindowsTimeZoneId))
        {
            throw new ArgumentException(
                "The existing schema requires epoch, computer, channel, provider, record ID, and time zone.",
                nameof(item));
        }
    }

    private static bool IsBusyOrLocked(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;

    private static string ToStorageSource(WindowsEventSource source) => source switch
    {
        WindowsEventSource.SysmonDelete => "sysmon_delete",
        WindowsEventSource.SysmonProcess => "sysmon_process",
        WindowsEventSource.Security4663 => "security_4663",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported source.")
    };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
