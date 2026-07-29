using System.Globalization;
using System.Security.Cryptography;
using DeleteAudit.Application.Projection;
using DeleteAudit.Application.Viewing;
using DeleteAudit.Domain;
using DeleteAudit.Infrastructure.LiveMonitoring;
using DeleteAudit.Infrastructure.Viewing;
using Microsoft.Data.Sqlite;

namespace DeleteAudit.Infrastructure.Projection;

/// <summary>
/// Canonically projects already-persisted live evidence into the live-owned tables
/// introduced by migration 0005.
/// </summary>
/// <remarks>
/// The service never creates a database or applies a migration. Projection writes use a
/// single transaction and target only <c>live_channel_epochs</c>,
/// <c>live_projected_records</c> and <c>live_projection_runs</c>. Queries and structural
/// readiness checks use read-only SQLite connections.
/// </remarks>
public sealed partial class SqliteLiveProjectionService
    : ILiveProjectionService, IDisposable
{
    private const int MaximumFailureCodeCharacters = 128;
    private const int MaximumFailureDetailCharacters = 2_048;
    private readonly ViewerDataLocation _location;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private bool _disposed;

    public SqliteLiveProjectionService(
        ViewerDataLocation location,
        TimeProvider? timeProvider = null)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LiveProjectionAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string databasePath;
        try
        {
            databasePath = _location.EnsureDatabasePath();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return new LiveProjectionAvailability(
                LiveProjectionState.Inaccessible,
                $"投影数据库路径不可用：{Bound(exception.Message)}",
                []);
        }

        if (!File.Exists(databasePath))
        {
            return new LiveProjectionAvailability(
                LiveProjectionState.MissingDatabase,
                "查看器数据库不存在；运行时不会自动创建数据库或应用 migration。",
                ["database"]);
        }

        try
        {
            // A weakened source ledger is not an acceptable projection input. This
            // prerequisite validation is read-only and remains separate from 0005.
            var sourceRepository = new SqliteLiveMonitoringRepository(_location);
            await sourceRepository.ValidateSchemaAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var connection = _location.CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var table in LiveProjectionSchema.Tables)
            {
                await SqliteLiveMonitoringRepository
                    .ValidateTableAsync(connection, table, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var trigger in LiveProjectionSchema.Triggers)
            {
                await SqliteLiveMonitoringRepository
                    .ValidateTriggerAsync(connection, trigger, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new LiveProjectionAvailability(
                LiveProjectionState.Ready,
                "实时规范投影已就绪；该能力独立于离线导入链。",
                []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            return new LiveProjectionAvailability(
                LiveProjectionState.MissingSchema,
                Bound(exception.Message),
                FindMissingObjects(exception.Message));
        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or UnauthorizedAccessException)
        {
            return new LiveProjectionAvailability(
                LiveProjectionState.Inaccessible,
                $"无法只读验证实时投影：{Bound(exception.Message)}",
                []);
        }
    }

    public async Task<LiveProjectionRunResult> ProjectSessionAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(liveSessionId);

        await _projectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var availability = await GetAvailabilityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!availability.IsReady)
            {
                return Failed(
                    liveSessionId,
                    0,
                    0,
                    "projection_unavailable",
                    availability.Message);
            }

            var startedUtc = _timeProvider.GetUtcNow();
            try
            {
                return await ProjectCoreAsync(
                        liveSessionId,
                        startedUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProjectionFailureException exception)
            {
                await TryRecordFailureAsync(
                        liveSessionId,
                        startedUtc,
                        exception.Code,
                        exception.Message,
                        exception.ConsideredCount,
                        exception.SkippedCount,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Failed(
                    liveSessionId,
                    exception.ConsideredCount,
                    exception.SkippedCount,
                    exception.Code,
                    exception.Message);
            }
            catch (Exception exception) when (
                exception is SqliteException
                    or IOException
                    or UnauthorizedAccessException
                    or CryptographicException
                    or FormatException
                    or InvalidOperationException)
            {
                const string code = "projection_failed";
                var detail = Bound(exception.Message);
                await TryRecordFailureAsync(
                        liveSessionId,
                        startedUtc,
                        code,
                        detail,
                        0,
                        0,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Failed(liveSessionId, 0, 0, code, detail);
            }
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    public async Task<LiveContinuityStatus> VerifyContinuityAsync(
        string liveSessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(liveSessionId);

        var availability = await GetAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!availability.IsReady)
        {
            return Broken(
                liveSessionId,
                0,
                null,
                null,
                $"投影不可用：{availability.Message}");
        }

        try
        {
            await using var connection = _location.CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            if (!await SessionExistsAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return Broken(
                    liveSessionId,
                    0,
                    null,
                    null,
                    "找不到指定的实时接入会话。");
            }

            var status = await VerifyContinuityCoreAsync(
                    connection,
                    transaction,
                    liveSessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or IOException
                or UnauthorizedAccessException
                or CryptographicException
                or FormatException)
        {
            return Broken(
                liveSessionId,
                0,
                null,
                null,
                $"连续性验证失败：{Bound(exception.Message)}");
        }
    }

    public async Task<PageResult<LiveProjectedRecordRow>> GetProjectedRecordsAsync(
        LiveProjectionQuery query,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();

        var availability = await GetAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!availability.IsReady)
        {
            throw new InvalidOperationException(availability.Message);
        }

        await using var connection = _location.CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        const string where = """
            live_session_id = $session
              AND ($source IS NULL OR source = $source)
              AND ($path IS NULL
                   OR normalized_path LIKE $path ESCAPE '\')
              AND ($process IS NULL
                   OR process_path LIKE $process ESCAPE '\'
                   OR process_guid LIKE $process ESCAPE '\'
                   OR command_line LIKE $process ESCAPE '\')
            """;

        long total;
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText =
                $"SELECT COUNT(*) FROM live_projected_records WHERE {where};";
            AddQueryParameters(count, query);
            total = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                live_projection_id,
                live_evidence_id,
                live_session_id,
                live_channel_epoch_id,
                source_received_sequence,
                live_ingest_sequence,
                event_record_id,
                provider_name,
                channel_name,
                machine_name,
                event_utc,
                observed_utc,
                source,
                normalized_path,
                object_kind,
                process_id,
                process_path,
                process_guid,
                command_line,
                parent_process_id,
                parent_process_path,
                parent_process_guid,
                user_name,
                user_sid,
                delete_permission,
                archive_expected,
                missing_fields_json,
                hex(raw_xml_sha256),
                hex(canonical_payload_sha256),
                hex(entry_hash),
                CASE WHEN previous_entry_hash IS NULL
                     THEN NULL
                     ELSE hex(previous_entry_hash)
                END,
                projected_utc
            FROM live_projected_records
            WHERE {where}
            ORDER BY live_ingest_sequence {(query.Descending ? "DESC" : "ASC")},
                     live_projection_id {(query.Descending ? "DESC" : "ASC")}
            LIMIT $limit OFFSET $offset;
            """;
        AddQueryParameters(command, query);
        command.Parameters.Add("$limit", SqliteType.Integer).Value = query.Page.Limit;
        command.Parameters.Add("$offset", SqliteType.Integer).Value = query.Page.Offset;

        var rows = new List<LiveProjectedRecordRow>(query.Page.Limit);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadProjectedRow(reader));
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PageResult<LiveProjectedRecordRow>(
            rows,
            total,
            query.Page.Offset,
            query.Page.Limit);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Do not dispose the gate: an in-flight request may still be unwinding after
        // its caller cancelled during window close and must be able to release safely.
        // SemaphoreSlim owns no unmanaged resource; new calls are rejected above.
    }

    private static LiveProjectedRecordRow ReadProjectedRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            LiveProjectionCanonicalizer.GetNullableInt64(reader, 6),
            LiveProjectionCanonicalizer.GetNullableString(reader, 7),
            reader.GetString(8),
            LiveProjectionCanonicalizer.GetNullableString(reader, 9),
            LiveProjectionCanonicalizer.GetNullableTimestamp(reader, 10),
            LiveProjectionCanonicalizer.ParseTimestamp(reader.GetString(11)),
            reader.GetString(12),
            LiveProjectionCanonicalizer.GetNullableString(reader, 13),
            LiveProjectionCanonicalizer.GetNullableString(reader, 14),
            LiveProjectionCanonicalizer.GetNullableInt32(reader, 15),
            LiveProjectionCanonicalizer.GetNullableString(reader, 16),
            LiveProjectionCanonicalizer.GetNullableString(reader, 17),
            LiveProjectionCanonicalizer.GetNullableString(reader, 18),
            LiveProjectionCanonicalizer.GetNullableInt32(reader, 19),
            LiveProjectionCanonicalizer.GetNullableString(reader, 20),
            LiveProjectionCanonicalizer.GetNullableString(reader, 21),
            LiveProjectionCanonicalizer.GetNullableString(reader, 22),
            LiveProjectionCanonicalizer.GetNullableString(reader, 23),
            LiveProjectionCanonicalizer.GetNullableString(reader, 24),
            LiveProjectionCanonicalizer.GetNullableBoolean(reader, 25),
            reader.GetString(26),
            reader.GetString(27).ToUpperInvariant(),
            reader.GetString(28).ToUpperInvariant(),
            reader.GetString(29).ToUpperInvariant(),
            LiveProjectionCanonicalizer
                .GetNullableString(reader, 30)
                ?.ToUpperInvariant(),
            LiveProjectionCanonicalizer.ParseTimestamp(reader.GetString(31)));

    private static void AddQueryParameters(
        SqliteCommand command,
        LiveProjectionQuery query)
    {
        command.Parameters.Add("$session", SqliteType.Text).Value = query.LiveSessionId;
        command.Parameters.Add("$source", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(query.Source);
        command.Parameters.Add("$path", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(ToLikePattern(query.PathContains));
        command.Parameters.Add("$process", SqliteType.Text).Value =
            LiveProjectionCanonicalizer.ToDb(ToLikePattern(query.ProcessContains));
    }

    private static string? ToLikePattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var escaped = value
            .Trim()
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    private static LiveProjectionRunResult Failed(
        string liveSessionId,
        long considered,
        long skipped,
        string code,
        string detail) =>
        new(
            liveSessionId,
            considered,
            0,
            skipped,
            false,
            LiveProjectionCanonicalizer.Bound(code, MaximumFailureCodeCharacters),
            Bound(detail));

    private static LiveContinuityStatus Broken(
        string liveSessionId,
        long projectedCount,
        long? sequence,
        string? evidenceId,
        string detail) =>
        new(
            liveSessionId,
            projectedCount,
            false,
            sequence,
            evidenceId,
            Bound(detail));

    private static string Bound(string value) =>
        LiveProjectionCanonicalizer.Bound(
            value,
            MaximumFailureDetailCharacters);

    private static string[] FindMissingObjects(string message) =>
        LiveProjectionSchema.Tables
            .Select(table => table.Name)
            .Concat(LiveProjectionSchema.Triggers.Select(trigger => trigger.Name))
            .Where(name => message.Contains(name, StringComparison.OrdinalIgnoreCase))
            .DefaultIfEmpty("live projection prerequisite")
            .ToArray();

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
