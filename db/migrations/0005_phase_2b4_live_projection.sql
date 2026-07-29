-- DeleteAudit Phase 2B.4 live-owned canonical projection.
-- Apply explicitly after db/schema.sql, 0002_phase_1b_offline_import.sql,
-- 0003_phase_2a_live_monitoring.sql and 0004_phase_2b_live_evidence.sql. Runtime code
-- must not apply migrations; it only validates that these objects exist and fails closed
-- when they do not.
--
-- Scope note: this increment normalises live-captured evidence into a canonical shape
-- that belongs entirely to the live path. It deliberately does NOT write into, extend or
-- reuse the offline structures. The decisions recorded in 0003 and 0004 stand unchanged:
--
--   * nothing here is written to raw_events, delete_events, delete_sessions or
--     channel_epochs;
--   * no offline ingest_sequence is consumed and no offline entry_hash chain is extended;
--   * no import_session, input-file SHA-256, offline channel epoch, offline start_reason
--     or offline hash-chain anchor is fabricated.
--
-- A live channel epoch here means exactly one thing: the span of one capture session on
-- one channel of one machine. It is NOT a claim about the real lifetime of the Windows
-- event log, and it must never be presented as one.
--
-- The continuity hash is an ordering and accidental-modification aid for this
-- application's own writes. It is not a cryptographic tamper-proofing guarantee: anyone
-- who can write to the database file can rewrite a chain end to end. See SECURITY.md.

-- One capture session's span on one channel of one machine.
CREATE TABLE live_channel_epochs (
    live_channel_epoch_id   TEXT PRIMARY KEY,
    live_session_id         TEXT NOT NULL
                                REFERENCES live_capture_sessions(live_session_id),
    channel_name            TEXT NOT NULL CHECK (length(channel_name) > 0),
    -- Both stay exactly as the channel reported them; neither is inferred.
    machine_name            TEXT,
    provider_name           TEXT,
    opened_utc              TEXT NOT NULL,
    -- The receive position of the first record attributed to this epoch.
    first_received_sequence INTEGER NOT NULL CHECK (first_received_sequence > 0),
    -- One epoch per session, channel and reported machine. A session that sees two
    -- machine names on one channel gets two epochs rather than one blurred together.
    UNIQUE (live_session_id, channel_name, machine_name)
) STRICT;

CREATE INDEX ix_live_channel_epochs_session
    ON live_channel_epochs(live_session_id);

-- The canonical projection of one captured record.
CREATE TABLE live_projected_records (
    -- Derived from the evidence it projects, so replaying a projection can only ever
    -- collide with itself rather than create a second copy.
    live_projection_id      TEXT PRIMARY KEY,
    live_evidence_id        TEXT NOT NULL UNIQUE
                                REFERENCES live_capture_records(live_evidence_id),
    live_session_id         TEXT NOT NULL
                                REFERENCES live_capture_sessions(live_session_id),
    live_channel_epoch_id   TEXT NOT NULL
                                REFERENCES live_channel_epochs(live_channel_epoch_id),
    -- Preserved from live_capture_records. This is the receive position, including any
    -- gaps left by records that were dropped, oversized, ignored or could not be parsed.
    source_received_sequence
                            INTEGER NOT NULL CHECK (source_received_sequence > 0),
    -- Scoped to the session, not global: this sequence orders one capture and makes no
    -- claim about any other capture or about the offline ingest sequence. Unlike the
    -- source sequence, it is dense across projectable records.
    live_ingest_sequence    INTEGER NOT NULL CHECK (live_ingest_sequence > 0),
    event_record_id         INTEGER,
    provider_name           TEXT,
    channel_name            TEXT NOT NULL CHECK (length(channel_name) > 0),
    machine_name            TEXT,
    event_utc               TEXT,
    observed_utc            TEXT NOT NULL,
    source                  TEXT NOT NULL CHECK (source IN
                               ('sysmon_delete', 'sysmon_process', 'security_4663')),
    parser_raw_event_id     TEXT,
    parsed_event_id         INTEGER,
    normalized_path         TEXT,
    object_kind             TEXT CHECK (object_kind IS NULL
                                        OR object_kind IN
                                           ('unknown', 'file', 'directory')),
    process_id              INTEGER,
    process_path            TEXT,
    process_guid            TEXT,
    command_line            TEXT,
    parent_process_id       INTEGER,
    parent_process_path     TEXT,
    parent_process_guid     TEXT,
    user_name               TEXT,
    user_sid                TEXT,
    delete_permission       TEXT CHECK (delete_permission IS NULL
                                        OR delete_permission IN
                                           ('not_observed', 'delete',
                                            'delete_child',
                                            'delete_and_delete_child')),
    archive_expected        INTEGER CHECK (archive_expected IS NULL
                                           OR archive_expected IN (0, 1)),
    -- Deterministic JSON array of the field names the parser explicitly reported missing.
    missing_fields_json     TEXT NOT NULL,
    raw_xml_sha256          BLOB NOT NULL CHECK (length(raw_xml_sha256) = 32),
    -- Digest of every canonical projection field above. The continuity entry hash covers
    -- this value as well as the source XML digest.
    canonical_payload_sha256
                            BLOB NOT NULL
                                CHECK (length(canonical_payload_sha256) = 32),
    -- NULL only for the first record of a session's chain; that anchor is explicit
    -- rather than implied, and it never points at an offline chain.
    previous_entry_hash     BLOB CHECK (previous_entry_hash IS NULL
                                        OR length(previous_entry_hash) = 32),
    entry_hash              BLOB NOT NULL UNIQUE CHECK (length(entry_hash) = 32),
    projected_utc           TEXT NOT NULL,
    UNIQUE (live_session_id, source_received_sequence),
    UNIQUE (live_session_id, live_ingest_sequence),
    CHECK ((live_ingest_sequence = 1 AND previous_entry_hash IS NULL)
           OR (live_ingest_sequence > 1 AND length(previous_entry_hash) = 32))
) STRICT;

CREATE INDEX ix_live_projected_records_epoch
    ON live_projected_records(live_channel_epoch_id, live_ingest_sequence);
CREATE INDEX ix_live_projected_records_source
    ON live_projected_records(source, observed_utc);
CREATE INDEX ix_live_projected_records_session_source_sequence
    ON live_projected_records(live_session_id, source_received_sequence);

-- One projection attempt over one capture session.
CREATE TABLE live_projection_runs (
    live_projection_run_id  TEXT PRIMARY KEY,
    live_session_id         TEXT NOT NULL
                                REFERENCES live_capture_sessions(live_session_id),
    started_utc             TEXT NOT NULL,
    completed_utc           TEXT NOT NULL,
    outcome                 TEXT NOT NULL CHECK (outcome IN ('completed', 'failed')),
    considered_count        INTEGER NOT NULL CHECK (considered_count >= 0),
    projected_count         INTEGER NOT NULL CHECK (projected_count >= 0),
    -- Records already projected by an earlier run. Replaying is expected, not an error.
    skipped_count           INTEGER NOT NULL CHECK (skipped_count >= 0),
    failure_code            TEXT CHECK (failure_code IS NULL
                                        OR (length(failure_code) > 0
                                            AND length(failure_code) <= 128)),
    failure_detail          TEXT CHECK (failure_detail IS NULL
                                        OR length(failure_detail) <= 2048),
    CHECK (considered_count >= projected_count + skipped_count),
    -- A completed run explains nothing; a failed run must say why.
    CHECK ((outcome = 'completed' AND failure_code IS NULL)
           OR (outcome = 'failed' AND failure_code IS NOT NULL))
) STRICT;

CREATE INDEX ix_live_projection_runs_session
    ON live_projection_runs(live_session_id, started_utc);

-- Append-only guards for the new tables only. As in 0004 they protect against
-- application mistakes, not against a local administrator who can replace the file.
CREATE TRIGGER live_channel_epochs_no_update
BEFORE UPDATE ON live_channel_epochs BEGIN
    SELECT RAISE(ABORT, 'live_channel_epochs is append-only');
END;
CREATE TRIGGER live_channel_epochs_no_delete
BEFORE DELETE ON live_channel_epochs BEGIN
    SELECT RAISE(ABORT, 'live_channel_epochs is append-only');
END;

CREATE TRIGGER live_projected_records_no_update
BEFORE UPDATE ON live_projected_records BEGIN
    SELECT RAISE(ABORT, 'live_projected_records is append-only');
END;
CREATE TRIGGER live_projected_records_no_delete
BEFORE DELETE ON live_projected_records BEGIN
    SELECT RAISE(ABORT, 'live_projected_records is append-only');
END;

CREATE TRIGGER live_projection_runs_no_update
BEFORE UPDATE ON live_projection_runs BEGIN
    SELECT RAISE(ABORT, 'live_projection_runs is append-only');
END;
CREATE TRIGGER live_projection_runs_no_delete
BEFORE DELETE ON live_projection_runs BEGIN
    SELECT RAISE(ABORT, 'live_projection_runs is append-only');
END;
