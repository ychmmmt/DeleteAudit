# Security Policy

## Project status

DeleteAudit is **Alpha / experimental** software. It is a research and learning
project, not a product.

**Do not rely on DeleteAudit for production forensics, incident response, legal
evidence, or compliance.** It does not provide a complete or tamper-evident
delete audit trail, and it cannot prevent, block, or recover deletions.

## What the current release does

- Offline analysis of Windows event data that the user explicitly imports
  (a single `.xml` or `.evtx` file at a time).
- A **live ingestion preview**: after the user clicks "start", the application
  subscribes read-only, in-process, to Windows event log channels that already
  exist on the machine (`Microsoft-Windows-Sysmon/Operational` and `Security`),
  filtered server-side to Sysmon 1/23/26 and Security 4663.
- Live capture (Phase 2B.2.1) stores, in the local viewer database, the raw XML,
  parsing and classification results, and related live evidence for each supported
  event received after you start it, plus a session summary. Correlation results,
  delete sessions and risk results are still **not** produced or stored for live
  capture, and there is no Live History UI.

## What it deliberately does not do

- It does not install or configure Sysmon.
- It does not modify Windows audit policy, the registry, certificates,
  services, or scheduled tasks.
- It does not request administrator elevation.
- It does not write to or clear any event log.
- It does not connect to remote event logs.
- It does not start automatically or run in the background.
- It does not upload anything anywhere.

## Files on a network share

Importing a file that lives on a network share is allowed, but the boundary is
narrower than it may look, so it is stated here explicitly:

- When you browse to or select a network share in the **Windows file picker**,
  Windows may already have contacted that location and checked whether the path or
  file exists. That happens before DeleteAudit is given the path.
- The confirmation prompt DeleteAudit shows afterwards controls only whether
  **this application** goes on to check, open and read the selected file.
- If you confirm, reading that file **does** produce network access. This is not a
  claim that no network access occurs.
- Cancelling stops DeleteAudit from doing anything further, but it **cannot undo**
  whatever the Windows file picker may already have done.
- DeleteAudit neither stores nor asks you for network credentials.

Copying the file to your own machine first avoids all of this. The user-facing
explanation is in the README:
[中文](README.md#关于网络共享上的文件) · [English](README.en.md#about-files-on-a-network-share).

## What live capture stores, and what that is worth

- Live detail is written **only to the local viewer database** inside the
  repository's `artifacts` directory. Nothing is uploaded.
- A batch enters persistence immediately at 64 records. A partial batch is normally
  scheduled for persistence about five seconds after its first record enters an empty
  batch; later records in the same batch do not restart that deadline. This is not a
  strict five-second guarantee: operating-system and thread scheduling, SQLite I/O, or
  a fault can make completion later.
- The record can still have **bounded gaps**: an abrupt process termination may lose
  up to 63 uncommitted records, and queue overflow, an oversized event, or a failed
  write also leaves a gap. A capture session with no completion row did not finish
  cleanly and must be read that way.
- A completion save is attempted once and is not retried automatically. If it fails,
  the session is shown as `Error`; records committed successfully before that failure
  are kept.
- The database is **not a tamper-proof medium**. A local administrator, or any
  process with write access to the file, can modify, replace or delete it. There is
  no signature, no external anchoring and no tamper-evident chain for live capture.
- The append-only SQLite triggers constrain **this application's own write path**. They
  are not a defence against another program that can write to the file: such a writer
  chooses its own connection settings, and SQLite fires delete triggers for `REPLACE`
  conflict resolution only when `recursive_triggers` is enabled. DeleteAudit never
  issues `REPLACE` and does enable that setting on its own write connection, but neither
  fact constrains anyone else. Read the triggers as protection against application
  mistakes, not against an attacker who already has write access.
- Consequently DeleteAudit must **not** be treated as a sole or authoritative source
  of evidence.

## Supported versions

Only the latest commit on the default branch receives fixes. There are no
backports and no long-term support branches at this stage.

## Reporting a vulnerability

Please use **GitHub Private Vulnerability Reporting** on this repository
(Security → Report a vulnerability). That keeps the report private until a fix
is available.

Please do not open a public issue for a suspected vulnerability, and please do
not include real event log data, real machine names, real user names, or real
SIDs in a report — a synthetic reproduction is always preferred.

We aim to acknowledge reports within a reasonable time, but because this is a
volunteer Alpha project there is no guaranteed response window.

## Scope notes

Findings that describe the documented Alpha limitations above (for example
"there is no Live History UI" or "there is no tamper-evident chain for live
capture") are known design boundaries, not vulnerabilities. Findings that
show the application exceeding its documented boundaries — for example reading
an event log channel without user action, escaping its controlled data
directory, or writing outside the repository — are in scope and welcome.
