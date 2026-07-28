-- DeleteAudit Phase 2B.1 live evidence schema increment.
-- Apply explicitly after db/schema.sql, 0002_phase_1b_offline_import.sql and
-- 0003_phase_2a_live_monitoring.sql. Runtime code must not apply migrations; it only
-- validates that these objects exist and fails closed when they do not.
--
-- Scope note: these tables hold the detail of what a user-initiated, in-process,
-- read-only live capture actually received. They deliberately carry their own identity:
--
--   live_evidence_id = live_session_id + ':' + received_sequence
--
-- No offline identity is reused. There is no import_session_id, no input-file SHA-256,
-- no channel_epoch_id, no offline ingest_sequence and no offline entry_hash here.
-- parser_raw_event_id is the parser's content-derived id and raw_xml_sha256 is a
-- content digest; neither is a signature, an external anchor, or a tamper-evident chain.
-- Correlation, delete sessions and risk stay owned by the offline path: Phase 2B.1
-- persists received evidence and its classification, nothing more.
--
-- A session row with no matching completion row means the capture did not finish
-- cleanly — it may have been terminated abnormally. It must never be reinterpreted as
-- a normal stop.

CREATE TABLE live_capture_sessions (
    live_session_id         TEXT PRIMARY KEY,
    started_utc             TEXT NOT NULL,
    queue_capacity          INTEGER NOT NULL CHECK (queue_capacity > 0),
    application_version     TEXT NOT NULL CHECK (length(application_version) > 0)
) STRICT;

CREATE TABLE live_capture_records (
    live_evidence_id        TEXT PRIMARY KEY,
    live_session_id         TEXT NOT NULL
                                REFERENCES live_capture_sessions(live_session_id),
    -- Assigned on the delivery thread, before the queue decision, so a dropped or
    -- oversized record consumes a sequence and leaves a visible gap.
    received_sequence       INTEGER NOT NULL CHECK (received_sequence > 0),
    -- Provided by the channel; may be absent and can never stand alone as identity.
    event_record_id         INTEGER,
    provider_name           TEXT,
    channel_name            TEXT NOT NULL CHECK (length(channel_name) > 0),
    machine_name            TEXT,
    time_created_utc        TEXT,
    observed_utc            TEXT NOT NULL,
    raw_xml                 TEXT NOT NULL
                                CHECK (length(raw_xml) > 0
                                       AND length(raw_xml) <= 1048576),
    raw_xml_sha256          BLOB NOT NULL CHECK (length(raw_xml_sha256) = 32),
    -- The parser's stable content id. Null when parsing produced no identifiable event.
    parser_raw_event_id     TEXT CHECK (parser_raw_event_id IS NULL
                                        OR length(parser_raw_event_id) > 0),
    parsed_event_id         INTEGER,
    outcome                 TEXT NOT NULL CHECK (outcome IN
                               ('delete_fact', 'process_context', 'security_evidence',
                                'ignored', 'error')),
    error_code              TEXT CHECK (error_code IS NULL
                                        OR (length(error_code) > 0
                                            AND length(error_code) <= 128)),
    detail                  TEXT CHECK (detail IS NULL OR length(detail) <= 2048),
    UNIQUE (live_session_id, received_sequence)
) STRICT;

CREATE INDEX ix_live_capture_records_observed
    ON live_capture_records(observed_utc);
CREATE INDEX ix_live_capture_records_outcome
    ON live_capture_records(outcome, observed_utc);

CREATE TABLE live_capture_completions (
    live_session_id         TEXT PRIMARY KEY
                                REFERENCES live_capture_sessions(live_session_id),
    stopped_utc             TEXT NOT NULL,
    final_state             TEXT NOT NULL CHECK (final_state IN ('stopped', 'error')),
    received_count          INTEGER NOT NULL CHECK (received_count >= 0),
    delete_fact_count       INTEGER NOT NULL CHECK (delete_fact_count >= 0),
    process_context_count   INTEGER NOT NULL CHECK (process_context_count >= 0),
    security_evidence_count INTEGER NOT NULL CHECK (security_evidence_count >= 0),
    ignored_count           INTEGER NOT NULL CHECK (ignored_count >= 0),
    error_count             INTEGER NOT NULL CHECK (error_count >= 0),
    dropped_count           INTEGER NOT NULL CHECK (dropped_count >= 0),
    late_discarded_count    INTEGER NOT NULL CHECK (late_discarded_count >= 0),
    suppressed_diagnostic_count
                            INTEGER NOT NULL
                                CHECK (suppressed_diagnostic_count >= 0),
    -- How many live_capture_records rows this session actually committed.
    persisted_record_count  INTEGER NOT NULL CHECK (persisted_record_count >= 0),
    -- The Phase 2A balance is preserved unchanged: every accepted record is accounted
    -- for exactly once. late_discarded_count stays outside it by design.
    CHECK (received_count =
           delete_fact_count + process_context_count + security_evidence_count
           + ignored_count + error_count + dropped_count),
    -- Records that never reached the queue (oversized, dropped) cannot have been
    -- persisted, so the persisted count can never exceed the classified ones.
    CHECK (persisted_record_count <=
           delete_fact_count + process_context_count + security_evidence_count
           + ignored_count + error_count)
) STRICT;

CREATE INDEX ix_live_capture_completions_stopped
    ON live_capture_completions(stopped_utc);

-- Append-only guards for the new tables only. They protect against application
-- mistakes, not against a local administrator who can replace the database file.
CREATE TRIGGER live_capture_sessions_no_update
BEFORE UPDATE ON live_capture_sessions BEGIN
    SELECT RAISE(ABORT, 'live_capture_sessions is append-only');
END;
CREATE TRIGGER live_capture_sessions_no_delete
BEFORE DELETE ON live_capture_sessions BEGIN
    SELECT RAISE(ABORT, 'live_capture_sessions is append-only');
END;

CREATE TRIGGER live_capture_records_no_update
BEFORE UPDATE ON live_capture_records BEGIN
    SELECT RAISE(ABORT, 'live_capture_records is append-only');
END;
CREATE TRIGGER live_capture_records_no_delete
BEFORE DELETE ON live_capture_records BEGIN
    SELECT RAISE(ABORT, 'live_capture_records is append-only');
END;

CREATE TRIGGER live_capture_completions_no_update
BEFORE UPDATE ON live_capture_completions BEGIN
    SELECT RAISE(ABORT, 'live_capture_completions is append-only');
END;
CREATE TRIGGER live_capture_completions_no_delete
BEFORE DELETE ON live_capture_completions BEGIN
    SELECT RAISE(ABORT, 'live_capture_completions is append-only');
END;
