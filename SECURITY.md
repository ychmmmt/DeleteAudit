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
- Only a **session summary** is stored for live monitoring. Live event XML,
  delete facts, correlation results and risk results are **not** stored.

## What it deliberately does not do

- It does not install or configure Sysmon.
- It does not modify Windows audit policy, the registry, certificates,
  services, or scheduled tasks.
- It does not request administrator elevation.
- It does not write to or clear any event log.
- It does not connect to remote event logs.
- It does not start automatically or run in the background.
- It does not upload anything anywhere.

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
"live event detail is not persisted" or "there is no tamper-evident chain for
live capture") are known design boundaries, not vulnerabilities. Findings that
show the application exceeding its documented boundaries — for example reading
an event log channel without user action, escaping its controlled data
directory, or writing outside the repository — are in scope and welcome.
