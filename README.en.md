# DeleteAudit

[简体中文](README.md) · **English** · [Filipino](README.fil.md)

> An open-source log viewing and organising tool for Windows. **Alpha / experimental.**

DeleteAudit is an open-source log viewing and organising tool for Windows. It imports supported local log files, helps you read what was imported, and — only after you switch it on yourself — previews live log ingestion on your own machine. The project is still Alpha: it is good for learning, testing and trying out a workflow, and **it should not be treated as a complete or production-grade forensic system**.

## What this is

Windows records events like "this file was deleted" in its system logs, but those logs are scattered, raw and hard to read. DeleteAudit takes one log file **that you pick yourself**, and turns it into a list you can actually browse: when, which file, which program, which user.

It is a tool for **looking at and organising** logs. It is not a protection tool. It cannot stop a deletion, and it cannot bring a deleted file back.

## What it does

- **Offline import** — one `.xml` or `.evtx` log file at a time, chosen by you.
- **Browse and organise** — page through imported results by time, path and status, and open the delete events and their raw evidence.
- **Live ingestion (Phase 2B.2.1)** — after you press start on the live page, it subscribes read-only to log channels that already exist on your machine. **From the moment you start it, the raw XML, parsing and classification results, and related live evidence for each supported event it receives are written to a local SQLite database**, and detail that was committed successfully **is kept** after you stop or close the app. A session summary is saved as well.
- **Import records** — every import produces a record and a manifest file, so you can check exactly what went in.

## Who it is for

**A good fit if** you want to see what Windows delete-related logs actually look like, you need one log file turned into a readable list, or you want to learn from or contribute to a tool like this.

**Not a fit yet if** you need something for production or a real investigation, you need to prevent accidental deletion or block an attacker, or you want a download-and-run installer.

## Current status and limits

- Stage: **Alpha / experimental**, published at **Phase 2A**.
- Systems: **Windows 10 / Windows 11**.
- Runtime: **.NET 8**.
- **Not a complete or production-grade forensic system**, and not held to the standard of a commercial digital forensics product.
- It cannot prevent accidental deletion, and it cannot stop a determined attacker or evidence tampering.
- **There is no live history screen yet.** Newly stored live detail is not projected onto the "delete events" or "raw evidence" pages; for now it can only be queried directly from the database.
- Live ingestion currently receives, classifies and stores; correlation, session aggregation and risk assessment are **not wired into** the live path yet and are deferred to a later part of **Phase 2B**.
- **Writes have a fixed batch deadline, not a strict timing guarantee.** A batch enters persistence immediately at 64 records. A partial batch is normally scheduled for persistence about five seconds after its first record enters an empty batch; later records in that batch do not restart the deadline. Operating-system and thread scheduling, SQLite I/O, or a fault can make completion later.
- **There can still be gaps.** An abrupt process termination can still lose up to 63 uncommitted records; queue overflow, oversized events, or a failed write also leave gaps. A session with no completion record means that capture did not finish cleanly.
- **The completion record is attempted once, with no automatic retry.** If that save fails, the session shows `Error`; records committed successfully beforehand are kept.
- **No signature, no external anchoring, no tamper-evident chain.** The database is not a tamper-proof medium.
- This repository ships **source code only**. There is **no** ready-to-run, signed Windows installer.
- Latest verification: **229 unit tests, 105 integration tests, 334 in total, all passing**, with a 0 warning / 0 error build.

Per-phase acceptance records, the design overview and the threat model live in [`docs/`](docs/).

## Privacy and safety boundaries

Please read this part properly:

- **Nothing is quietly uploaded.** DeleteAudit does not send your data anywhere on the internet.
- **It does not read live logs by default.** A channel is only subscribed to after you press start yourself.
- **It does not connect to remote Windows event logs** — only to channels that already exist on this machine.
- **It does not go scanning or enumerating network locations**, and it does not walk your drives.
- **It does not install Sysmon, change audit policy, touch the registry, ask for administrator rights, or stay resident in the background.**
- **It neither stores nor asks you for network credentials.**
- Certain internal system paths (device paths, the ones written like `\\?\` or `\\.\`) are **rejected outright** and cannot be imported.

### About files on a network share

If the file you pick sits on a network share (for example `\\server\share\log.evtx`), keep two things apart:

When you browse to or select a network share in the Windows file picker, **Windows may already have connected to that share** and checked whether the path or file exists. The confirmation prompt that appears afterwards only controls whether **DeleteAudit** goes on to read and import the file. Choosing Cancel stops DeleteAudit from doing anything further, but it **cannot undo** whatever the system file picker may already have done.

The prompt defaults to Cancel, and Escape cancels too. Every network share you pick is asked about again — the answer is never remembered. **The easiest and safest route is to copy the file to your own machine first**, then import it locally.

To be clear: this is **not** the same as "no network access ever happens". Once you confirm, reading that shared file really does go over the network.

## Running it, or working on it

For now you build it from source. You need Windows 10/11, the [.NET 8 SDK](https://dotnet.microsoft.com/download) and Git.

```bash
git clone <repository-url>
cd DeleteAudit
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Run the viewer:

```bash
dotnet run --project src/DeleteAudit.Viewer
```

Data and output stay inside the repository's `artifacts\` folder; nothing is written outside the checkout. Build rules, test rules and directory conventions are in [CONTRIBUTING.md](CONTRIBUTING.md).

## Reporting a security problem

Please use this repository's **GitHub Private Vulnerability Reporting** (Security → Report a vulnerability) rather than opening a public issue. The full policy is in [SECURITY.md](SECURITY.md).

When you report, please **do not** attach real log data, real machine names or real user names — a synthetic reproduction is always preferred.

## Contributing and licence

Issues and pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) first.

This project uses the **MIT License**. You may use it for personal or commercial purposes, and you may modify and redistribute it, as long as you keep the licence and copyright notice. Full text in [LICENSE](LICENSE).
