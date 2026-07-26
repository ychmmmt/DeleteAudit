-- DeleteAudit Phase 1B offline import schema increment.
-- Apply explicitly after db/schema.sql; runtime code must not apply migrations.

CREATE TABLE import_sessions (
    import_session_id       TEXT PRIMARY KEY,
    source_kind             TEXT NOT NULL CHECK (source_kind IN ('multi_xml', 'evtx')),
    original_file_name      TEXT NOT NULL CHECK (length(original_file_name) > 0),
    normalized_source_path  TEXT NOT NULL CHECK (length(normalized_source_path) > 0),
    file_size_bytes         INTEGER NOT NULL CHECK (file_size_bytes >= 0),
    source_last_write_utc   TEXT NOT NULL,
    source_sha256           BLOB NOT NULL UNIQUE CHECK (length(source_sha256) = 32),
    started_utc             TEXT NOT NULL,
    completed_utc           TEXT,
    total_record_count      INTEGER NOT NULL DEFAULT 0 CHECK (total_record_count >= 0),
    success_record_count    INTEGER NOT NULL DEFAULT 0 CHECK (success_record_count >= 0),
    ignored_record_count    INTEGER NOT NULL DEFAULT 0 CHECK (ignored_record_count >= 0),
    error_record_count      INTEGER NOT NULL DEFAULT 0 CHECK (error_record_count >= 0),
    application_version     TEXT NOT NULL CHECK (length(application_version) > 0),
    schema_version          INTEGER NOT NULL CHECK (schema_version >= 1),
    status                  TEXT NOT NULL CHECK (status IN
                               ('in_progress', 'completed', 'partial_failure', 'failed')),
    output_status           TEXT CHECK (output_status IS NULL OR output_status IN
                               ('prepared', 'complete', 'failed')),
    jsonl_output_path       TEXT,
    jsonl_output_sha256     BLOB CHECK (
                               jsonl_output_sha256 IS NULL
                               OR length(jsonl_output_sha256) = 32),
    manifest_output_path    TEXT,
    output_error_code       TEXT,
    output_error_message    TEXT,
    CHECK (
        total_record_count =
            success_record_count + ignored_record_count + error_record_count),
    CHECK (
        (status = 'in_progress' AND completed_utc IS NULL)
        OR (status <> 'in_progress' AND completed_utc IS NOT NULL))
) STRICT;

CREATE INDEX ix_import_sessions_started
    ON import_sessions(started_utc);
CREATE INDEX ix_import_sessions_status
    ON import_sessions(status, started_utc);

CREATE TABLE import_records (
    import_session_id       TEXT NOT NULL REFERENCES import_sessions(import_session_id),
    record_ordinal          INTEGER NOT NULL CHECK (record_ordinal > 0),
    raw_xml_state           TEXT NOT NULL CHECK (raw_xml_state IN
                               ('captured', 'unavailable')),
    raw_xml                 TEXT,
    raw_xml_sha256          BLOB,
    outcome                 TEXT NOT NULL CHECK (outcome IN
                               ('success', 'ignored', 'error')),
    raw_event_id            TEXT REFERENCES raw_events(raw_event_id),
    CHECK (
        (raw_xml_state = 'captured'
         AND raw_xml IS NOT NULL
         AND raw_xml_sha256 IS NOT NULL
         AND length(raw_xml_sha256) = 32)
        OR
        (raw_xml_state = 'unavailable'
         AND raw_xml IS NULL
         AND raw_xml_sha256 IS NULL)),
    PRIMARY KEY (import_session_id, record_ordinal)
) WITHOUT ROWID, STRICT;

CREATE INDEX ix_import_records_raw_event
    ON import_records(raw_event_id)
    WHERE raw_event_id IS NOT NULL;
CREATE INDEX ix_import_records_outcome
    ON import_records(import_session_id, outcome, record_ordinal);

CREATE TABLE import_diagnostics (
    import_diagnostic_id    TEXT PRIMARY KEY,
    import_session_id       TEXT NOT NULL REFERENCES import_sessions(import_session_id),
    record_ordinal          INTEGER CHECK (
                               record_ordinal IS NULL OR record_ordinal > 0),
    stage                   TEXT NOT NULL CHECK (stage IN
                               ('source_validation', 'read', 'extract', 'parse',
                                'normalize', 'correlate', 'persist', 'jsonl',
                                'manifest')),
    severity                TEXT NOT NULL CHECK (severity IN
                               ('info', 'warning', 'error')),
    code                    TEXT NOT NULL CHECK (length(code) > 0),
    message                 TEXT NOT NULL CHECK (length(message) > 0),
    details_json            TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(details_json)),
    occurred_utc            TEXT NOT NULL,
    FOREIGN KEY (import_session_id, record_ordinal)
        REFERENCES import_records(import_session_id, record_ordinal)
) STRICT;

CREATE INDEX ix_import_diagnostics_session_record
    ON import_diagnostics(import_session_id, record_ordinal, occurred_utc);
CREATE INDEX ix_import_diagnostics_severity
    ON import_diagnostics(import_session_id, severity, occurred_utc);

CREATE TABLE event_correlations (
    event_correlation_id          TEXT PRIMARY KEY,
    delete_event_id               TEXT NOT NULL UNIQUE
                                      REFERENCES delete_events(delete_event_id),
    matched_process_raw_event_id  TEXT REFERENCES raw_events(raw_event_id),
    matched_security_raw_event_id TEXT REFERENCES raw_events(raw_event_id),
    method                        TEXT NOT NULL CHECK (method IN
                                      ('none', 'process_guid',
                                       'device_pid_user_and_time',
                                       'path_and_time_heuristic')),
    confidence                    TEXT NOT NULL CHECK (confidence IN
                                      ('none', 'low', 'medium', 'high')),
    time_delta_ms                 INTEGER CHECK (
                                      time_delta_ms IS NULL OR time_delta_ms >= 0),
    identity_fields_enriched      INTEGER NOT NULL CHECK (
                                      identity_fields_enriched IN (0, 1)),
    reasons_json                  TEXT NOT NULL CHECK (json_valid(reasons_json)),
    created_utc                   TEXT NOT NULL,
    CHECK (
        (method = 'none'
         AND confidence = 'none'
         AND time_delta_ms IS NULL
         AND matched_process_raw_event_id IS NULL
         AND matched_security_raw_event_id IS NULL
         AND identity_fields_enriched = 0)
        OR
        (method <> 'none'
         AND confidence <> 'none'
         AND time_delta_ms IS NOT NULL
         AND (matched_process_raw_event_id IS NOT NULL
              OR matched_security_raw_event_id IS NOT NULL))),
    CHECK (
        method <> 'path_and_time_heuristic'
        OR identity_fields_enriched = 0)
) STRICT;

CREATE INDEX ix_event_correlations_process_raw
    ON event_correlations(matched_process_raw_event_id)
    WHERE matched_process_raw_event_id IS NOT NULL;
CREATE INDEX ix_event_correlations_security_raw
    ON event_correlations(matched_security_raw_event_id)
    WHERE matched_security_raw_event_id IS NOT NULL;
CREATE INDEX ix_event_correlations_confidence
    ON event_correlations(confidence, created_utc);

CREATE TABLE risk_assessment_subject_links (
    risk_assessment_id      TEXT PRIMARY KEY
                                REFERENCES risk_assessments(risk_assessment_id),
    delete_session_id       TEXT REFERENCES delete_sessions(delete_session_id),
    delete_event_id         TEXT REFERENCES delete_events(delete_event_id),
    CHECK (
        (delete_session_id IS NOT NULL AND delete_event_id IS NULL)
        OR
        (delete_session_id IS NULL AND delete_event_id IS NOT NULL))
) STRICT;

CREATE INDEX ix_risk_subject_links_session
    ON risk_assessment_subject_links(delete_session_id)
    WHERE delete_session_id IS NOT NULL;
CREATE INDEX ix_risk_subject_links_event
    ON risk_assessment_subject_links(delete_event_id)
    WHERE delete_event_id IS NOT NULL;
