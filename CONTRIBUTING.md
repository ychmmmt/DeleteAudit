# Contributing to DeleteAudit

Thanks for your interest. DeleteAudit is Alpha software; please read
[SECURITY.md](SECURITY.md) for what it does and does not claim to do.

## Prerequisites

- **Windows 10 or Windows 11.** The viewer is WPF and the event log adapters use
  `System.Diagnostics.EventLog`, so the solution builds and tests on Windows only.
- **.NET 8 SDK** (`net8.0` / `net8.0-windows`).
- Git.

## Getting started

```bash
git clone <your-fork-url>
cd DeleteAudit
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

The repository root is resolved automatically by walking up from the build
output until `DeleteAudit.sln` is found, so you can clone anywhere. To point the
application at a different root explicitly, set:

```bash
set DELETEAUDIT_REPOSITORY_ROOT=C:\path\to\your\checkout
```

If neither the environment variable nor the solution lookup resolves a root, the
application fails closed with an explicit error rather than guessing a path.

## Build rules

- `TreatWarningsAsErrors` is **true** and `AnalysisLevel` is `latest-recommended`.
  A warning fails the build; please fix the cause rather than suppressing it.
- `Nullable` and `ImplicitUsings` are enabled solution-wide.
- Keep the build at **0 warnings, 0 errors**.

## Test rules

These are hard requirements, not style preferences:

- **Tests must never read the real Windows event log**, install Sysmon, change
  audit policy, or touch the registry. Live monitoring is tested through injected
  fakes only.
- No `Skip`, no retry loops, no fixed `Thread.Sleep`/`Task.Delay` for
  synchronisation. Use a real signal (`ManualResetEventSlim`,
  `TaskCompletionSource`) with a timeout that only guards against a hung test.
- Do not weaken an assertion to make a test pass, and do not copy production
  logic into a test.
- Tests write only under `<repository-root>\artifacts\`. Never write to
  `C:\ProgramData`, a user profile, or any absolute path outside the checkout.
- Clean up resources deterministically; async tests should `await` their work.

## Do not commit

The `.gitignore` already covers these, but please double-check before pushing:

- `artifacts/` (SDK, NuGet cache, build output, test output, viewer data)
- `releases/`
- `bin/`, `obj/`, `TestResults/`
- `*.db`, `*.db-shm`, `*.db-wal`
- JSONL output, `.evtx` files, or any real Windows event log data

**Never commit real event log data.** Test fixtures must be synthetic — see
`tests/Fixtures/` for the expected style (fictional machine names, users and
paths). Do not include real machine names, user names, SIDs, IP addresses, or
personal file paths in code, tests, fixtures, or commit messages.

## NuGet configuration

`NuGet.Config` redirects the global packages folder to `artifacts\nuget-packages`
inside the checkout:

```xml
<add key="globalPackagesFolder" value="artifacts\nuget-packages" />
```

This keeps every restore artifact inside the repository (and inside
`.gitignore`) instead of your user profile. The trade-off is that each clone
maintains its own package cache and the first restore is not shared with other
projects. Package source mapping restricts all packages to `nuget.org`.

## Pull requests

- Keep changes scoped; explain the "why" in the description.
- Include tests for behaviour changes.
- Confirm `dotnet build`, `dotnet test` and `git diff --check` are clean.
- By contributing you agree that your contribution is licensed under the
  [MIT License](LICENSE).
