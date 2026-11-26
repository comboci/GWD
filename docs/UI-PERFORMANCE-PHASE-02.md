# GWD UI Performance Phase 02 — IIS, SQL and I/O
Base: V268 + Phase 01 (069134c899f1a12a50cc949e8e7864a9b4f6493c). Implementation commit: ccdc12336e5edc54772810df361900201ff3ece6. Candidate only; no production deploy or version bump.

## Implemented
- IIS inventory: 3 read-only AppCmd XML commands for sites/apps/vdirs and 1 for pools in compatible fast path instead of 2+4S+P processes. Exact case-insensitive root join, strict attributes, duplicate/missing/DTD/truncation rejection; legacy fallback on unexpected schema or command exit. No full configuration dump. Actual IIS XML verification on Windows remains mandatory. Reference: https://learn.microsoft.com/en-us/iis/get-started/getting-started-with-iis/getting-started-with-appcmdexe
- IIS page: worker-based inventory, cancellation on superseding refresh/disposal, latest-request-only publication, no selection during inventory, cleared grids on reported refresh error. Destructive cleanup safety blocks unchanged.
- SQL discovery: registry metadata off UI thread, pre-cancellation supported, no added authentication probes. TCP/login mode metadata off-thread; SQL diagnostics fingerprint and generation guard. Original SQL strategy and fallback preserved.
- Hosting Bundle inspection off-thread at three call sites.
- Clean source export: worker and responsive modal progress (other operations remain excluded); one traversal instead of two, reparse checks/exclusions retained, same-directory temporary archive promoted only after closure. Previous archive not deleted before successful compression. No cancel control in this phase.
- CLI, SmokeTests, CodeQuality.Tests and CodeQuality.WindowsTests target .NET 10 to match their direct references. This resolves the identified framework mismatch; full build not claimed. Independent older net9 test projects not changed.
- New static check: node tests/verify-ui-performance-phase-02.cjs. New isolated Windows smoke option --ui-performance-phase-02 covers XML fixtures, malformed XML, pre-cancellation, and archive preservation.

## Evidence and limitations
Equivalent static source assertions and exact checks of the deployment method, two IIS destructive safety methods and SQL authentication method passed during editing. No .NET SDK or Windows/IIS/SQL runtime here; compilation and Windows smoke tests not executed. Old tests/verify-static.cjs has historic net9/hash assumptions; no full legacy suite success claimed. No measured speed improvement claimed.

## Windows acceptance
1. dotnet build DeploymentManager.sln -c Release
2. dotnet run --project src/DeploymentManager.SmokeTests -c Release -- --ui-performance-phase-02
3. Compare actual bulk inventory and legacy output on isolated IIS with child apps, non-ASCII names, empty lists, invalid/truncated XML, and refresh cancellation/disposal. Count processes and measure p50/p95. No production mutations.
4. Check SQL default/named instance discovery, changing credentials/instance during diagnostics; run --sql-strategy and --diagnostic-history. No authentication change.
5. Test clean ZIP with existing destination, locked source file, slow disk, excluded folders, linked source; check responsive progress and preserved old archive on failure.
6. Re-run Phase 01 checks and benchmark navigation/log responsiveness.

## Remaining
Settings persistence queue needs a dedicated ordered design covering JSON read-modify-write, registry, direct writes and final flush. Do not move it blindly into independent workers. Remaining source/target detection, scan/profile coordination, health-check coalescing, log virtualization, page architecture and visual overhaul are open. No release or merge until build and runtime evidence are available.
