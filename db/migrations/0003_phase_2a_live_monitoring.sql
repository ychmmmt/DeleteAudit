-- DeleteAudit Phase 2A live monitoring schema increment.
-- Apply explicitly after db/schema.sql and 0002_phase_1b_offline_import.sql.
-- Runtime code must not apply migrations; it only validates that these objects exist.
--
-- Scope note: these tables record that a user-initiated, in-process, read-only preview
-- session ran and what it classified. Live-captured evidence is NOT written here:
-- raw XML, delete facts, correlation, delete sessions and risk remain owned by the
-- offline import path. Phase 2B will design a dedicated live evidence identity rather
-- than reuse an import session, a file SHA-256, a channel epoch, or the offline hash
-- chain anchor.

CREATE TABLE live_monitoring_sessions (
    live_session_id         TEXT PRIMARY KEY,
    started_utc             TEXT NOT NULL,
    stopped_utc             TEXT,
    final_state             TEXT NOT NULL CHECK (final_state IN ('stopped', 'error')),
    received_count          INTEGER NOT NULL DEFAULT 0 CHECK (received_count >= 0),
    -- Classification counts are stored separately: only delete_fact_count represents
    -- an observed delete. Process context and security evidence must never be
    -- presented, summed, or reported as deletes.
    delete_fact_count       INTEGER NOT NULL DEFAULT 0 CHECK (delete_fact_count >= 0),
    process_context_count   INTEGER NOT NULL DEFAULT 0 CHECK (process_context_count >= 0),
    security_evidence_count INTEGER NOT NULL DEFAULT 0 CHECK (security_evidence_count >= 0),
    ignored_count           INTEGER NOT NULL DEFAULT 0 CHECK (ignored_count >= 0),
    error_count             INTEGER NOT NULL DEFAULT 0 CHECK (error_count >= 0),
    dropped_count           INTEGER NOT NULL DEFAULT 0 CHECK (dropped_count >= 0),
    -- Records that arrived after the session stopped accepting. They belonged to no
    -- live session, so they are tracked outside the balance equation below.
    late_discarded_count    INTEGER NOT NULL DEFAULT 0 CHECK (late_discarded_count >= 0),
    suppressed_diagnostic_count
                            INTEGER NOT NULL DEFAULT 0
                                CHECK (suppressed_diagnostic_count >= 0),
    queue_capacity          INTEGER NOT NULL CHECK (queue_capacity > 0),
    application_version     TEXT NOT NULL CHECK (length(application_version) > 0),
    -- Every accepted record is accounted for exactly once.
    CHECK (received_count =
           delete_fact_count + process_context_count + security_evidence_count
           + ignored_count + error_count + dropped_count),
    CHECK (stopped_utc IS NULL OR stopped_utc >= started_utc)
) STRICT;

CREATE INDEX ix_live_monitoring_sessions_started
    ON live_monitoring_sessions(started_utc);

CREATE TABLE live_monitoring_channels (
    live_session_id         TEXT NOT NULL
                                REFERENCES live_monitoring_sessions(live_session_id),
    channel_name            TEXT NOT NULL CHECK (length(channel_name) > 0),
    availability            TEXT NOT NULL CHECK (availability IN
                               ('available', 'unavailable', 'access_denied',
                                'disabled', 'unknown_error')),
    detail                  TEXT,
    PRIMARY KEY (live_session_id, channel_name)
) WITHOUT ROWID, STRICT;

CREATE TABLE live_monitoring_diagnostics (
    live_diagnostic_id      TEXT PRIMARY KEY,
    live_session_id         TEXT NOT NULL
                                REFERENCES live_monitoring_sessions(live_session_id),
    stage                   TEXT NOT NULL CHECK (stage IN
                               ('probe', 'subscribe', 'receive', 'queue',
                                'parse', 'persist')),
    severity                TEXT NOT NULL CHECK (severity IN
                               ('info', 'warning', 'error')),
    code                    TEXT NOT NULL CHECK (length(code) > 0),
    -- Bounded at the application layer; enforced here so a long message can never be
    -- stored even if that layer regresses.
    message                 TEXT NOT NULL CHECK (
                               length(message) > 0 AND length(message) <= 2048),
    occurred_utc            TEXT NOT NULL
) STRICT;

CREATE INDEX ix_live_monitoring_diagnostics_session
    ON live_monitoring_diagnostics(live_session_id, occurred_utc);
CREATE INDEX ix_live_monitoring_diagnostics_severity
    ON live_monitoring_diagnostics(severity, occurred_utc);
