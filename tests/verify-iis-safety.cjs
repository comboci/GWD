const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const read = p => fs.readFileSync(path.join(root,p), 'utf8');
const engine = read('src/DeploymentManager/Services/DeploymentService.cs');
const cli = read('src/DeploymentManager.Cli/Program.cs');
const wizard = read('src/DeploymentManager/WizardForm.cs');
let n=0;
function check(ok, name) { if (!ok) throw Error(name); console.log('PASS ' + name); n++; }
function section(text, a, b) {
    const start = text.indexOf(a), end = text.indexOf(b,start+a.length);
    if(start < 0 || end <= start) throw Error('Missing safety section: '+a+' / '+b);
    return text.slice(start,end);
}
function ordered(text, ...anchors) {
    let offset = 0;
    for (const anchor of anchors) {
        const i = text.indexOf(anchor, offset);
        if (i < 0) return false;
        offset = i + anchor.length;
    }
    return true;
}
const activate = section(engine, 'public async Task<string> ActivateStagingAsync', 'private async Task RestoreHostedFolderSafelyAsync');
check(ordered(activate,'ValidateIisPreflightAsync','CreateUniqueRollbackFolder'), 'preflight precedes backup creation');
check(ordered(activate,'await StopSiteForDeploymentAsync','CopyDirectory(stagingFolder'), 'checked STOP precedes file preparation');
check(ordered(activate,'CopyDirectory(stagingFolder','await WaitForAppPoolStoppedAsync(profile, ct);','Directory.Move(fullTarget'), 'STOP revalidated after staging copy and before swap');
check(!engine.includes('delete site ') && !engine.includes('add site '), 'deploy and recovery never delete or recreate sites');
check(!engine.includes('iisreset') && !engine.includes('Restart-Service'), 'no IIS reset or service restart in deployment engine');
const recovery = section(engine,'public async Task CleanupFailedDeploymentAsync','public void MarkDeploymentSuccessful');
check(!recovery.includes('ConfigureIisAsync'), 'file rollback does not reconfigure bindings/site');
check(ordered(recovery,'FILES_RESTORED / IIS_UNHEALTHY','try { await RestoreOriginalIisStatesAsync(profile); }','var recoveredHealth = await HealthCheckAsync(profile, recoveryToken)'), 'file result recorded before IIS recovery');
check(ordered(recovery,'if (!_filesChanged)','await RestoreOriginalIisStatesAsync(profile);','FILES_UNCHANGED / ORIGINAL_IIS_STATES_VERIFIED / HTTP_NOT_CHECKED'), 'failed partial STOP cannot trigger blind file restore');
const wait = section(engine,'private async Task WaitForAppPoolStoppedAsync','private async Task DeleteDirectoryWithRetryAsync');
check(wait.includes('site != "Stopped" || pool != "Stopped"') && wait.includes('throw new IOException'), 'unknown/non-stopped site or pool cannot authorize files');
check(!wait.includes('continuing with') && !wait.includes('ExitCode != 0 ||'), 'old failed-query and timeout PASS paths removed');
const deployCli = section(cli,'private static async Task<CliResult> DeployAsync','private static async Task<CliResult> RollbackAsync');
check(ordered(deployCli,'BeginIisAttempt','PublishWithQualityAsync') && deployCli.includes('CleanupFailedDeploymentAsync'), 'CLI holds target lease before build and compensates failed activation/start');
const deployWizard = section(wizard,'private async Task DeployAsync','private async Task NotifyDeploymentOutcomeAsync');
check(ordered(deployWizard,'BeginIisAttempt','PublishAsync(_profile') && deployWizard.includes('iisAttempt?.Dispose()'), 'wizard holds target lease before build through recovery');
check(!deployWizard.includes('publish is null && _profile.TargetMode != "Remote IIS Server") await _deployment.RestartAppAsync'), 'failed preflight no longer blindly restarts IIS');
check(read('src/DeploymentManager/Services/DiagnosticBundleService.cs').includes('"iis-attempts"'), 'attempt evidence included in diagnostics bundle');
console.log(`${n} integration source guards passed (static; no IIS operations).`);
