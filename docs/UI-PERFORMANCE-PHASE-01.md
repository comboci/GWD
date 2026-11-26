# GWD UI Performance — Phase 01 (V268 baseline candidate)

Base: 5f709d4129304a5cb902205e0b3cf8f53db097a7. No release/version bump or deployment.

## Changes
- Navigation readiness uses a keyed snapshot (mode/path) rather than repeatedly enumerating project files during ordinary UI events. Existing explicit Source and Prepared Publish checks supply the snapshot; uncached selections are probed on a debounced worker. Superseded results cannot publish.
- Existing Next, target, deployment and rollback validation paths remain authoritative. The snapshot is only a UI readiness hint, not authorization to deploy. Reset and failed Next invalidate it.
- Review refresh disposes the old view via the existing log-preserving cleanup path, with layout suspended.
- Diagnostics Current Log and report tabs enumerate/read in workers; generation guards reject obsolete results. Preview is bounded to 500,000 characters, with explicit truncation notice; original files remain accessible by open/export. Existing export implementation unchanged.
- Added independent static source-contract check: `node tests/verify-ui-performance.cjs`.

## Evidence and limitations
Static source anchors checked during edit. Windows build and runtime acceptance pending. No runtime speed improvement claimed. Explicit source/target detection and some prepared-publish validation still use synchronous I/O. Settings persistence, IIS N+1 query optimization, log rendering virtualization, full page lifecycle refactoring and visual redesign remain pending. Same-path changes require a fresh explicit validation; cached readiness must never replace authoritative deploy guards.

## Windows acceptance
1. Build Release on Windows with .NET 10 SDK; run existing SmokeTests and diagnostic-history tests.
2. Open a large source; edit app/site/database/repository fields repeatedly. Confirm navigation does not rescan the source on each edit. Switch between two folders/modes rapidly and confirm no stale enablement; invalid prepared artifacts must not pass.
3. Navigate Next, Auto Deploy in isolated dry-run/test context, return to source, Start Over. Verify original source/target/rollback guards and log continuity. Never deploy against production during audit.
4. Open Current Log and report tabs with synthetic 10 MB/100 MB files and many reports. Change selection rapidly, refresh and close during reads. Confirm responsive UI, preview truncation notice, original complete export and no stale preview.
5. Run repeat Review rebuild cycles; confirm the persistent log survives, disposed controls do not accumulate, and no cross-thread/unobserved exceptions.
