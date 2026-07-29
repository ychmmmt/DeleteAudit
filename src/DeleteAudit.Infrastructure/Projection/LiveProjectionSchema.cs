using DeleteAudit.Infrastructure.LiveMonitoring;

namespace DeleteAudit.Infrastructure.Projection;

internal static class LiveProjectionSchema
{
    internal const string Migration =
        "db/migrations/0005_phase_2b4_live_projection.sql";

    internal static IReadOnlyList<SqliteLiveMonitoringRepository.TableRequirement>
        Tables { get; } =
    [
        new(
            "live_channel_epochs",
            Migration,
            IsWithoutRowId: false,
            [
                C("live_channel_epoch_id", "TEXT", true, 1),
                C("live_session_id", "TEXT", true),
                C("channel_name", "TEXT", true),
                C("machine_name", "TEXT", false),
                C("provider_name", "TEXT", false),
                C("opened_utc", "TEXT", true),
                C("first_received_sequence", "INTEGER", true)
            ],
            [
                new SqliteLiveMonitoringRepository.ForeignKeyRequirement(
                    "live_session_id",
                    "live_capture_sessions",
                    "live_session_id")
            ],
            [
                new SqliteLiveMonitoringRepository.UniqueRequirement(
                    ["live_session_id", "channel_name", "machine_name"])
            ]),
        new(
            "live_projected_records",
            Migration,
            IsWithoutRowId: false,
            [
                C("live_projection_id", "TEXT", true, 1),
                C("live_evidence_id", "TEXT", true),
                C("live_session_id", "TEXT", true),
                C("live_channel_epoch_id", "TEXT", true),
                C("source_received_sequence", "INTEGER", true),
                C("live_ingest_sequence", "INTEGER", true),
                C("event_record_id", "INTEGER", false),
                C("provider_name", "TEXT", false),
                C("channel_name", "TEXT", true),
                C("machine_name", "TEXT", false),
                C("event_utc", "TEXT", false),
                C("observed_utc", "TEXT", true),
                C("source", "TEXT", true),
                C("parser_raw_event_id", "TEXT", false),
                C("parsed_event_id", "INTEGER", false),
                C("normalized_path", "TEXT", false),
                C("object_kind", "TEXT", false),
                C("process_id", "INTEGER", false),
                C("process_path", "TEXT", false),
                C("process_guid", "TEXT", false),
                C("command_line", "TEXT", false),
                C("parent_process_id", "INTEGER", false),
                C("parent_process_path", "TEXT", false),
                C("parent_process_guid", "TEXT", false),
                C("user_name", "TEXT", false),
                C("user_sid", "TEXT", false),
                C("delete_permission", "TEXT", false),
                C("archive_expected", "INTEGER", false),
                C("missing_fields_json", "TEXT", true),
                C("raw_xml_sha256", "BLOB", true),
                C("canonical_payload_sha256", "BLOB", true),
                C("previous_entry_hash", "BLOB", false),
                C("entry_hash", "BLOB", true),
                C("projected_utc", "TEXT", true)
            ],
            [
                new SqliteLiveMonitoringRepository.ForeignKeyRequirement(
                    "live_evidence_id",
                    "live_capture_records",
                    "live_evidence_id"),
                new SqliteLiveMonitoringRepository.ForeignKeyRequirement(
                    "live_session_id",
                    "live_capture_sessions",
                    "live_session_id"),
                new SqliteLiveMonitoringRepository.ForeignKeyRequirement(
                    "live_channel_epoch_id",
                    "live_channel_epochs",
                    "live_channel_epoch_id")
            ],
            [
                new SqliteLiveMonitoringRepository.UniqueRequirement(["live_evidence_id"]),
                new SqliteLiveMonitoringRepository.UniqueRequirement(
                    ["live_session_id", "source_received_sequence"]),
                new SqliteLiveMonitoringRepository.UniqueRequirement(
                    ["live_session_id", "live_ingest_sequence"]),
                new SqliteLiveMonitoringRepository.UniqueRequirement(["entry_hash"])
            ]),
        new(
            "live_projection_runs",
            Migration,
            IsWithoutRowId: false,
            [
                C("live_projection_run_id", "TEXT", true, 1),
                C("live_session_id", "TEXT", true),
                C("started_utc", "TEXT", true),
                C("completed_utc", "TEXT", true),
                C("outcome", "TEXT", true),
                C("considered_count", "INTEGER", true),
                C("projected_count", "INTEGER", true),
                C("skipped_count", "INTEGER", true),
                C("failure_code", "TEXT", false),
                C("failure_detail", "TEXT", false)
            ],
            [
                new SqliteLiveMonitoringRepository.ForeignKeyRequirement(
                    "live_session_id",
                    "live_capture_sessions",
                    "live_session_id")
            ],
            [])
    ];

    internal static IReadOnlyList<SqliteLiveMonitoringRepository.TriggerRequirement>
        Triggers { get; } =
    [
        T("live_channel_epochs_no_update", "live_channel_epochs", "UPDATE"),
        T("live_channel_epochs_no_delete", "live_channel_epochs", "DELETE"),
        T("live_projected_records_no_update", "live_projected_records", "UPDATE"),
        T("live_projected_records_no_delete", "live_projected_records", "DELETE"),
        T("live_projection_runs_no_update", "live_projection_runs", "UPDATE"),
        T("live_projection_runs_no_delete", "live_projection_runs", "DELETE")
    ];

    private static SqliteLiveMonitoringRepository.ColumnRequirement C(
        string name,
        string type,
        bool notNull,
        int primaryKeyOrdinal = 0) =>
        new(name, type, notNull, primaryKeyOrdinal);

    private static SqliteLiveMonitoringRepository.TriggerRequirement T(
        string name,
        string table,
        string operation) =>
        new(name, table, operation, Migration);
}
