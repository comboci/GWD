# GWD — Phase 03: Wizard view separation and lifecycle
Baseline: Phase 02 at ad494e7f1ddeed15cc1d5902cc5faf5e378651d3, V268 candidate. Implementation commit 36ac5d37738353a6204ed63513a332ebf3aa09b3.

## Changes
- WizardForm is now a partial class with SourceAndTarget (718 lines), InstallersAndSql (707), ReviewAndLocation (222), and core (2715 at implementation commit). The original core was 4184 lines; approximately 35% fewer lines in the main file. Partial class separation is a staging step, not full Presenter/UserControl extraction.
- Reselecting an already mounted step reuses the live controls. Forced rebuild is explicit for settings/installer refresh. Cross-page navigation still reconstructs controls to avoid stale profile/credential state. Review completion retains its original explicit rebuild with shared log detached first.
- Suspend/resume layout across page transitions, dispose prior view before changing page identity, invalidate render identity on teardown, preserve custom names on valid source re-entry. Remove forced GC from Start Over.
- UI navigation and BusyAsync reentrancy guards; do not release the Deployment-owned flag from a page operation. Preserve Cancel Deploy.
- Auto Deploy owns its Environment Scan rather than triggering a second scan via page entry. Snapshot results collected locally; reject cancelled/stale source and mode before publication; cancel source-background verification when leaving source, resetting or starting Auto Deploy.
- Scan and IIS status cards update existing labels instead of replacing controls. Installer rebuilds dispose old cards and restore scroll.
- Prior static source assertions now read the partial view files where appropriate; historical V70/V71 hashes still unchanged and not a current full regression baseline.

## Acceptance
Run on isolated Windows test machine (no production deployment):
1. dotnet build DeploymentManager.sln -c Release
2. node tests/verify-ui-performance.cjs
3. node tests/verify-ui-performance-phase-02.cjs
4. node tests/verify-ui-performance-phase-03.cjs
5. dotnet run --project src/DeploymentManager.SmokeTests -c Release -- --ui-performance-phase-03
6. Repeat original smoke, --ui-performance-phase-02, --sql-strategy and --diagnostic-history.
7. Verify current-step click reuse, Back/Next, Start Over, settings/installer forced refresh, profile load, source changes, cancellation, log survival, IIS/SQL operations, and page disposal while read-only background tasks are running. Verify one environment scan in Auto Deploy and preserved safety gates.
8. Measure navigation p50/p95, process/GDI handles, memory and UI stalls before/after.

## Limitations
Windows SDK/runtime unavailable in the editing environment; C# build and smoke tests not executed. Partial classes still share state; full lifecycle isolation, all asynchronous action cancellation/serialization, settings writer, log virtualization and visual modernization remain open. Some source detection/target validation and installer cache scans remain synchronous. No new release, merge or production deployment authorized. Performance improvements are structural, not measured.
