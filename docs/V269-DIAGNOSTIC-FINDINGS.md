# V269 diagnostic hotfix candidate

Reference: V268, commit 5f709d4129304a5cb902205e0b3cf8f53db097a7; GWD diagnostic bundle exported 2026-11-26 15:03 local time.

## Findings and changes

- Current NSP Office failure: V826 frontend TS2741. The IIS attempt journal records BUILD=FAILED, MutationStarted=false, FilesChanged=false, Recovery=NOT_ATTEMPTED. The application fix is separately committed in comboci/NSPOffice on fix/v826-bank-quickadd-build.
- Performance report incorrectly marked Frontend build DONE although the process exited 1. BuildPerformanceTrace now classifies nonzero CommandResult as FAILED without changing existing retry policies.
- Review banner displayed previous success and footer displayed in-progress during the error dialog. Both now update on state changes before error/cancel dialogs, including recovery failures.
- SMTP notification failed; original diagnostics intentionally did not contain SMTP cause. Record a safe, allow-listed failure category or numeric SMTP status, not raw replies, credentials or addresses. Actual SMTP delivery/configuration remains unverified.
- V267 GDI+ crash was in SplitContainer layout. Guard queued layout against stale/disposed/hidden controls and dispose replaced review pages while preserving the shared live log. This addresses observed lifecycle weaknesses; causal connection and runtime crash elimination are not proven.
- Historical disk exhaustion occurred in both prepared-release and restore copies on 2026-11-03 and 2026-11-17. Estimate activation and backup/rollback capacity before IIS STOP, recheck capacity before staged copy; do not attempt another payload copy when activation fails before any hosted-file mutation. Estimate includes conservative entry overhead and safety margin but does not reserve disk space or guarantee against concurrent use. No existing backup/release deletion.
- V268 successfully exported current-session.log and isolated eight rejected legacy JSONL rows. Latest completed history record is valid; do not rewrite, invent missing values or delete legacy evidence. No repeat fix applied.
- Historical startup 500.30 and compiler errors are application issues; no IIS restart, database restore or migration is required to address TS2741.

## Verification

Source changes and GitHub commits recorded. Windows/.NET SDK unavailable in the editing environment; full build, smoke tests, live IIS, SMTP and rollback NOT_RUN. V269 is a source candidate, not a verified executable.

On a Windows test environment:
```powershell
dotnet build DeploymentManager.sln -c Release
dotnet run --project src/DeploymentManager.SmokeTests -- --diagnostic-findings
dotnet run --project src/DeploymentManager.SmokeTests -- --diagnostic-history
dotnet run --project src/DeploymentManager.SmokeTests
```

Repeat an intentional nonzero build against an isolated sample; verify FAILED in trace and both UI indicators before dismissing dialog, unchanged hosted files, and email category without secrets. Test low capacity using a disposable test volume, not production. Exercise review-page resize and repeated navigation with no deployment. Use Test Email with the operator's settings; never log credentials.

No production files/database changed; no main merge or automated deployment.
