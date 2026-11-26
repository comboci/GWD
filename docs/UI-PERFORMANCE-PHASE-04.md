# GWD Phase 04 — Log, diagnostics, history and resource performance

Base: Phase 03 at 36503a96d213f12ff6ea94f17094a0bc2e538434 (V268 candidate). Implementation commits: f713e70, 7b942ba, 36f5ef5. No release, merge, production deployment, or version bump.

## Implemented
- Log viewer: 64 KiB read batches on a worker, UTF-8 incomplete-byte boundary handling, incremental offsets, initial 200,000-byte tail, reset on detected truncation, single-flight reads, stale generation rejection, explicit Pause/Resume and Jump to Latest, adjacent-style grouping and 500,000-character preview trim. Export copies an exact-length snapshot of the full active log and promotes a completed temporary file; no truncation of original evidence.
- Wizard log UI: pending queue capped at 2,048 entries, each display entry clipped around 4,096 characters (plus marker), per flush up to 256 entries and 64,000 characters of entry text, retained display history capped at 1,000,000 characters/5,000 entries, filter reconstruction capped at 300,000 characters. All full disk writes remain before UI processing; existing LogService unchanged.
- History: reduce JSONL events to one latest record per attempt while reading (strict greater-than timestamp preserves earlier stable ties); malformed rows remain isolated. Lightweight project catalog does not retain all projects' detailed records simultaneously. Both UI history grids use WinForms VirtualMode/CellValueNeeded, with existing Diagnostics 200-record pages and legacy expandable build stages/session log links. Project reads are cancellable, off-thread and discard stale results.
- Diagnostics ZIP: valid history JSONL streamed directly into ZIP, malformed redacted lines into delete-on-close spool and then rejected sidecar. Original JSONL never changed. History read/write failure now aborts the temporary export instead of publishing a potentially incomplete history. Arbitrary text retains whole-file redaction to protect multiline credentials; this still has a memory cost for large non-JSONL files.
- CPU/RAM: single in-flight sampling worker started on Shown, monotonic Stopwatch CPU elapsed interval, separate sample/history locks, recent-history projection for Diagnostics, 5-second sampling/display cadence, deferred disposal of active process sampling. Full 720-sample history export remains.
- New static guard: node tests/verify-ui-performance-phase-04.cjs. New Windows smoke flag: --ui-performance-phase-04.

## Acceptance
1. Build solution Release with Windows .NET 10 SDK.
2. Run source checks for phases 01–04; run SmokeTests with --ui-performance-phase-04, --diagnostic-history, --ui-performance-phase-02, --ui-performance-phase-03, --sql-strategy, and default smoke.
3. Use synthetic 100 MB/1 GB append-only UTF-8 logs, rapid appends, pause/reset/close, active export, file shrink, and cancellation. Compare original hashes and completed snapshots; record UI latency, GDI/handle counts and RSS.
4. Use large JSONL with multiple events per attempt, malformed rows and build stages. Check counts/ordering, page switching, rapid selection, expanded rows, log links, cancellation, all-event export and rejected sidecar.
5. Open CPU/RAM tab during build, close/reopen, ensure one sampler and no stale callbacks. Compare process CPU, private bytes and p50/p95 UI latency before/after; use an isolated Windows target and no production IIS/SQL changes.

## Limits
No .NET SDK or Windows runtime in the current editing environment. Source-level checks only; Windows build, runtime smoke, visual verification and measured performance remain pending. No persistent history index/cache; each requested project still scans its JSONL, and full lifetime summary/unique attempts remain in memory. Very long individual JSONL records still allocate their line; general diagnostic text retains whole-file redaction. Log viewer assumes UTF-8 append-only logs; same-length/longer replacement is not fully detected, and line styles can split at a chunk boundary. No modification to core deployment/rollback safety or disk-log writer. Settings writer, UI redesign and final runtime acceptance remain open.
