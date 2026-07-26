-- DeleteAudit SQLite schema, design baseline v1.
-- This file defines future runtime storage; the current phase does not create a database.

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA trusted_schema = OFF;

CREATE TABLE schema_migrations (
    version                 INTEGER PRIMARY KEY,
    applied_utc             TEXT NOT NULL,
    script_sha256           BLOB NOT NULL CHECK (length(script_sha256) = 32)
) STRICT;

-- Event Record IDs can be reused after a Windows event log is cleared.  An epoch
-- gives each lifetime of a channel its own namespace and makes gaps explicit.
CREATE TABLE channel_epochs (
    channel_epoch_id        TEXT PRIMARY KEY,
    computer_name           TEXT NOT NULL,
    channel_name            TEXT NOT NULL,
    provider_name           TEXT NOT NULL,
    started_utc             TEXT NOT NULL,
    first_record_id         INTEGER,
    previous_epoch_id       TEXT REFERENCES channel_epochs(channel_epoch_id),
    start_reason            TEXT NOT NULL CHECK (start_reason IN
                               ('initial', 'record_id_regression', 'log_cleared',
                                'channel_identity_changed', 'operator_reset')),
    coverage_gap            INTEGER NOT NULL DEFAULT 0 CHECK (coverage_gap IN (0, 1))
) STRICT;

CREATE TABLE raw_events (
    raw_event_id            TEXT PRIMARY KEY,
    channel_epoch_id        TEXT NOT NULL REFERENCES channel_epochs(channel_epoch_id),
    source                  TEXT NOT NULL CHECK (source IN
                               ('sysmon_delete', 'security_4663', 'sysmon_process',
                                'usn', 'collector_health')),
    computer_name           TEXT NOT NULL,
    channel_name            TEXT NOT NULL,
    provider_name           TEXT NOT NULL,
    event_id                INTEGER NOT NULL,
    event_record_id         INTEGER NOT NULL,
    event_utc               TEXT NOT NULL,
    event_local             TEXT NOT NULL,
    local_utc_offset_minutes INTEGER NOT NULL,
    windows_time_zone_id    TEXT NOT NULL,
    observed_utc            TEXT NOT NULL,
    raw_xml                 TEXT NOT NULL,
    raw_xml_sha256          BLOB NOT NULL CHECK (length(raw_xml_sha256) = 32),
    ingest_sequence         INTEGER NOT NULL UNIQUE,
    previous_entry_hash     BLOB CHECK (previous_entry_hash IS NULL OR length(previous_entry_hash) = 32),
    entry_hash              BLOB NOT NULL UNIQUE CHECK (length(entry_hash) = 32),
    format_version          INTEGER NOT NULL DEFAULT 1,
    UNIQUE (computer_name, channel_epoch_id, event_record_id)
) STRICT;

CREATE TABLE process_observations (
    process_observation_id  TEXT PRIMARY KEY,
    source_raw_event_id     TEXT NOT NULL UNIQUE REFERENCES raw_events(raw_event_id),
    process_guid            TEXT,
    boot_id                 TEXT,
    process_id              INTEGER NOT NULL CHECK (process_id >= 0),
    process_start_utc       TEXT NOT NULL,
    process_path            TEXT,
    command_line            TEXT,
    parent_process_guid     TEXT,
    parent_process_id       INTEGER CHECK (parent_process_id IS NULL OR parent_process_id >= 0),
    parent_process_path     TEXT,
    user_name               TEXT,
    user_sid                TEXT,
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32),
    CHECK (process_guid IS NOT NULL OR boot_id IS NOT NULL)
) STRICT;

CREATE INDEX ix_process_observations_pid_time
    ON process_observations(process_id, process_start_utc);
CREATE INDEX ix_process_observations_guid
    ON process_observations(process_guid) WHERE process_guid IS NOT NULL;

CREATE TABLE delete_sessions (
    delete_session_id       TEXT PRIMARY KEY,
    opened_utc              TEXT NOT NULL,
    last_event_utc          TEXT NOT NULL,
    sealed_utc              TEXT,
    process_identity        TEXT NOT NULL,
    process_id              INTEGER,
    process_guid            TEXT,
    user_sid                TEXT,
    path_scope              TEXT NOT NULL COLLATE NOCASE,
    confirmed_item_count    INTEGER NOT NULL DEFAULT 0 CHECK (confirmed_item_count >= 0),
    protected_item_count    INTEGER NOT NULL DEFAULT 0 CHECK (protected_item_count >= 0),
    current_risk            TEXT NOT NULL CHECK (current_risk IN
                               ('informational', 'warning', 'critical')),
    warning_emitted         INTEGER NOT NULL DEFAULT 0 CHECK (warning_emitted IN (0, 1)),
    critical_emitted        INTEGER NOT NULL DEFAULT 0 CHECK (critical_emitted IN (0, 1)),
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32)
) STRICT;

CREATE INDEX ix_delete_sessions_actor_time
    ON delete_sessions(process_identity, user_sid, last_event_utc);
CREATE INDEX ix_delete_sessions_scope_time
    ON delete_sessions(path_scope, last_event_utc);

CREATE TABLE delete_events (
    delete_event_id         TEXT PRIMARY KEY,
    primary_raw_event_id    TEXT NOT NULL REFERENCES raw_events(raw_event_id),
    delete_session_id       TEXT NOT NULL REFERENCES delete_sessions(delete_session_id),
    occurred_utc            TEXT NOT NULL,
    occurred_local          TEXT NOT NULL,
    local_utc_offset_minutes INTEGER NOT NULL,
    windows_time_zone_id    TEXT NOT NULL,
    event_record_id         INTEGER NOT NULL,
    source                  TEXT NOT NULL CHECK (source IN ('sysmon_23', 'sysmon_26')),
    source_event_id         INTEGER NOT NULL CHECK (source_event_id IN (23, 26)),
    full_path               TEXT NOT NULL,
    normalized_path         TEXT NOT NULL COLLATE NOCASE,
    object_kind             TEXT NOT NULL CHECK (object_kind IN ('file', 'directory', 'unknown')),
    volume_serial           TEXT,
    file_reference_number   TEXT,
    process_id              INTEGER CHECK (process_id IS NULL OR process_id >= 0),
    process_path            TEXT,
    process_guid            TEXT,
    command_line            TEXT,
    parent_process_id       INTEGER CHECK (parent_process_id IS NULL OR parent_process_id >= 0),
    parent_process_path     TEXT,
    parent_process_guid     TEXT,
    user_name               TEXT,
    user_sid                TEXT,
    delete_permission_type  TEXT NOT NULL CHECK (delete_permission_type IN
                               ('delete', 'delete_child', 'delete_and_delete_child',
                                'not_observed')),
    initial_risk            TEXT NOT NULL CHECK (initial_risk IN
                               ('informational', 'warning', 'critical')),
    attribution_confidence  INTEGER NOT NULL CHECK (attribution_confidence BETWEEN 0 AND 100),
    archive_expected        INTEGER NOT NULL DEFAULT 0 CHECK (archive_expected IN (0, 1)),
    archive_reference       TEXT,
    missing_fields_json     TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(missing_fields_json)),
    content_sha256          BLOB NOT NULL CHECK (length(content_sha256) = 32),
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32),
    UNIQUE (primary_raw_event_id)
) STRICT;

CREATE INDEX ix_delete_events_time ON delete_events(occurred_utc);
CREATE INDEX ix_delete_events_path ON delete_events(normalized_path, occurred_utc);
CREATE INDEX ix_delete_events_process ON delete_events(process_guid, process_id, occurred_utc);
CREATE INDEX ix_delete_events_user ON delete_events(user_sid, occurred_utc);
CREATE INDEX ix_delete_events_session ON delete_events(delete_session_id, occurred_utc);

-- All supporting signals are retained rather than merged destructively into the
-- primary delete row.  This makes attribution decisions reproducible.
CREATE TABLE event_evidence (
    event_evidence_id       TEXT PRIMARY KEY,
    delete_event_id         TEXT NOT NULL REFERENCES delete_events(delete_event_id),
    source_raw_event_id     TEXT NOT NULL REFERENCES raw_events(raw_event_id),
    evidence_kind           TEXT NOT NULL CHECK (evidence_kind IN
                               ('primary_delete', 'security_permission', 'process_enrichment',
                                'usn_confirmation', 'usn_gap_candidate')),
    correlation_score       INTEGER NOT NULL,
    correlation_confidence TEXT NOT NULL CHECK (correlation_confidence IN
                               ('confirmed', 'low_confidence', 'rejected')),
    reasons_json            TEXT NOT NULL CHECK (json_valid(reasons_json)),
    created_utc             TEXT NOT NULL,
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32),
    UNIQUE (delete_event_id, source_raw_event_id, evidence_kind)
) STRICT;

CREATE INDEX ix_event_evidence_source ON event_evidence(source_raw_event_id);

CREATE TABLE session_members (
    delete_session_id       TEXT NOT NULL REFERENCES delete_sessions(delete_session_id),
    delete_event_id         TEXT NOT NULL UNIQUE REFERENCES delete_events(delete_event_id),
    added_utc               TEXT NOT NULL,
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32),
    PRIMARY KEY (delete_session_id, delete_event_id)
) WITHOUT ROWID, STRICT;

CREATE TABLE risk_assessments (
    risk_assessment_id      TEXT PRIMARY KEY,
    subject_kind            TEXT NOT NULL CHECK (subject_kind IN ('delete_event', 'delete_session')),
    subject_id              TEXT NOT NULL,
    assessed_utc            TEXT NOT NULL,
    risk_level              TEXT NOT NULL CHECK (risk_level IN
                               ('informational', 'warning', 'critical')),
    rule_code               TEXT NOT NULL CHECK (rule_code IN
                               ('single_delete', 'protected_root', 'burst_30_in_10s',
                                'burst_100_in_10s', 'coverage_degraded', 'integrity_failure')),
    window_start_utc        TEXT,
    window_end_utc          TEXT,
    observed_count          INTEGER CHECK (observed_count IS NULL OR observed_count >= 0),
    details_json            TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(details_json)),
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32)
) STRICT;

CREATE INDEX ix_risk_assessments_subject
    ON risk_assessments(subject_kind, subject_id, assessed_utc);

CREATE TABLE alerts (
    alert_id                TEXT PRIMARY KEY,
    delete_session_id       TEXT REFERENCES delete_sessions(delete_session_id),
    delete_event_id         TEXT REFERENCES delete_events(delete_event_id),
    risk_assessment_id      TEXT NOT NULL REFERENCES risk_assessments(risk_assessment_id),
    created_utc             TEXT NOT NULL,
    alert_kind              TEXT NOT NULL CHECK (alert_kind IN
                               ('protected_root', 'bulk_delete', 'coverage_gap', 'integrity_failure')),
    delivery_state          TEXT NOT NULL CHECK (delivery_state IN
                               ('recorded', 'delivered', 'failed')),
    delivery_details_json   TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(delivery_details_json)),
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32),
    CHECK (delete_session_id IS NOT NULL OR delete_event_id IS NOT NULL)
) STRICT;

CREATE TABLE protected_roots (
    protected_root_id       TEXT PRIMARY KEY,
    display_path            TEXT NOT NULL,
    normalized_path         TEXT NOT NULL UNIQUE COLLATE NOCASE,
    archive_with_sysmon_23  INTEGER NOT NULL DEFAULT 0 CHECK (archive_with_sysmon_23 IN (0, 1)),
    enabled                 INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    config_version          INTEGER NOT NULL,
    created_utc             TEXT NOT NULL
) STRICT;

CREATE TABLE usn_checkpoints (
    usn_checkpoint_id       TEXT PRIMARY KEY,
    volume_name             TEXT NOT NULL,
    volume_serial           TEXT NOT NULL,
    journal_id              TEXT NOT NULL,
    next_usn                TEXT NOT NULL,
    observed_utc            TEXT NOT NULL,
    gap_detected            INTEGER NOT NULL DEFAULT 0 CHECK (gap_detected IN (0, 1)),
    gap_reason              TEXT,
    integrity_hash          BLOB NOT NULL CHECK (length(integrity_hash) = 32)
) STRICT;

CREATE INDEX ix_usn_checkpoints_volume_time
    ON usn_checkpoints(volume_serial, observed_utc);

CREATE TABLE integrity_checkpoints (
    integrity_checkpoint_id TEXT PRIMARY KEY,
    created_utc             TEXT NOT NULL,
    scope                   TEXT NOT NULL CHECK (scope IN ('hourly', 'daily', 'startup', 'manual_verify')),
    first_ingest_sequence   INTEGER,
    last_ingest_sequence    INTEGER,
    first_entry_hash        BLOB CHECK (first_entry_hash IS NULL OR length(first_entry_hash) = 32),
    last_entry_hash         BLOB CHECK (last_entry_hash IS NULL OR length(last_entry_hash) = 32),
    record_count            INTEGER NOT NULL CHECK (record_count >= 0),
    jsonl_file_sha256       BLOB CHECK (jsonl_file_sha256 IS NULL OR length(jsonl_file_sha256) = 32),
    previous_checkpoint_hash BLOB CHECK (previous_checkpoint_hash IS NULL OR length(previous_checkpoint_hash) = 32),
    checkpoint_hash         BLOB NOT NULL UNIQUE CHECK (length(checkpoint_hash) = 32),
    signature_algorithm     TEXT,
    signature               BLOB,
    signer_key_id           TEXT,
    external_anchor_state   TEXT NOT NULL DEFAULT 'not_configured' CHECK (external_anchor_state IN
                               ('not_configured', 'pending', 'anchored', 'failed'))
) STRICT;

-- Viewer projection. Raw XML remains in raw_events but is logically part of the
-- audit record through primary_raw_event_id.
CREATE VIEW v_delete_audit AS
SELECT
    d.delete_event_id,
    d.occurred_utc,
    d.occurred_local,
    d.local_utc_offset_minutes,
    d.windows_time_zone_id,
    d.event_record_id,
    d.source,
    d.source_event_id,
    d.full_path,
    d.object_kind,
    d.process_id,
    d.process_path,
    d.process_guid,
    d.command_line,
    d.parent_process_id,
    d.parent_process_path,
    d.parent_process_guid,
    d.user_name,
    d.user_sid,
    d.delete_permission_type,
    d.delete_session_id,
    COALESCE(
        (SELECT r.risk_level
           FROM risk_assessments r
          WHERE r.subject_kind = 'delete_event' AND r.subject_id = d.delete_event_id
          ORDER BY CASE r.risk_level
                     WHEN 'critical' THEN 3
                     WHEN 'warning' THEN 2
                     ELSE 1
                   END DESC,
                   r.assessed_utc DESC
          LIMIT 1),
        s.current_risk,
        d.initial_risk
    ) AS risk_level,
    d.attribution_confidence,
    d.missing_fields_json,
    r.raw_xml,
    r.raw_xml_sha256,
    d.content_sha256,
    d.integrity_hash
FROM delete_events d
JOIN raw_events r ON r.raw_event_id = d.primary_raw_event_id
JOIN delete_sessions s ON s.delete_session_id = d.delete_session_id;

-- Append-only guards. They prevent application mistakes, not a local administrator
-- who can replace the database; signatures and external anchors address that boundary.
CREATE TRIGGER raw_events_no_update
BEFORE UPDATE ON raw_events BEGIN
    SELECT RAISE(ABORT, 'raw_events is append-only');
END;
CREATE TRIGGER raw_events_no_delete
BEFORE DELETE ON raw_events BEGIN
    SELECT RAISE(ABORT, 'raw_events is append-only');
END;

CREATE TRIGGER process_observations_no_update
BEFORE UPDATE ON process_observations BEGIN
    SELECT RAISE(ABORT, 'process_observations is append-only');
END;
CREATE TRIGGER process_observations_no_delete
BEFORE DELETE ON process_observations BEGIN
    SELECT RAISE(ABORT, 'process_observations is append-only');
END;

CREATE TRIGGER delete_events_no_update
BEFORE UPDATE ON delete_events BEGIN
    SELECT RAISE(ABORT, 'delete_events is append-only');
END;
CREATE TRIGGER delete_events_no_delete
BEFORE DELETE ON delete_events BEGIN
    SELECT RAISE(ABORT, 'delete_events is append-only');
END;

CREATE TRIGGER event_evidence_no_update
BEFORE UPDATE ON event_evidence BEGIN
    SELECT RAISE(ABORT, 'event_evidence is append-only');
END;
CREATE TRIGGER event_evidence_no_delete
BEFORE DELETE ON event_evidence BEGIN
    SELECT RAISE(ABORT, 'event_evidence is append-only');
END;

CREATE TRIGGER session_members_no_update
BEFORE UPDATE ON session_members BEGIN
    SELECT RAISE(ABORT, 'session_members is append-only');
END;
CREATE TRIGGER session_members_no_delete
BEFORE DELETE ON session_members BEGIN
    SELECT RAISE(ABORT, 'session_members is append-only');
END;

CREATE TRIGGER risk_assessments_no_update
BEFORE UPDATE ON risk_assessments BEGIN
    SELECT RAISE(ABORT, 'risk_assessments is append-only');
END;
CREATE TRIGGER risk_assessments_no_delete
BEFORE DELETE ON risk_assessments BEGIN
    SELECT RAISE(ABORT, 'risk_assessments is append-only');
END;

CREATE TRIGGER integrity_checkpoints_no_update
BEFORE UPDATE ON integrity_checkpoints BEGIN
    SELECT RAISE(ABORT, 'integrity_checkpoints is append-only');
END;
CREATE TRIGGER integrity_checkpoints_no_delete
BEFORE DELETE ON integrity_checkpoints BEGIN
    SELECT RAISE(ABORT, 'integrity_checkpoints is append-only');
END;

CREATE TRIGGER delete_sessions_no_delete
BEFORE DELETE ON delete_sessions BEGIN
    SELECT RAISE(ABORT, 'delete_sessions cannot be deleted');
END;
CREATE TRIGGER delete_sessions_sealed_or_regressed
BEFORE UPDATE ON delete_sessions
WHEN OLD.sealed_utc IS NOT NULL
  OR NEW.confirmed_item_count < OLD.confirmed_item_count
  OR NEW.protected_item_count < OLD.protected_item_count
  OR (CASE NEW.current_risk WHEN 'critical' THEN 3 WHEN 'warning' THEN 2 ELSE 1 END)
     < (CASE OLD.current_risk WHEN 'critical' THEN 3 WHEN 'warning' THEN 2 ELSE 1 END)
BEGIN
    SELECT RAISE(ABORT, 'sealed sessions are immutable and risk/counts cannot regress');
END;
