// Owner-only source contract. This is not runtime or deployment evidence.
'use strict';
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const { read, section, tail, ordered } = require('./source-contract-r03.cjs');
const checkpoint = JSON.parse(read('audit/regression/final-remediation-08-checkpoint.json'));
assert.equal(checkpoint.task, 'GWD-FINAL-REMEDIATION-08');
assert.equal(checkpoint.candidate.status, 'SOURCE_COMPLETE / RUNTIME_PENDING');
assert.equal(checkpoint.candidate.version, JSON.parse(read('deployment-manager.version.json')).version);
assert.equal(checkpoint.stages.length, 8);
const git = (...args) => execFileSync('git', args, {encoding:'utf8'}).trim();
const lineage = git('rev-list','--reverse',checkpoint.baseline.commit+'..HEAD').split(/\r?\n/);
assert.equal(lineage.length,8,'Use the exact eight-stage release commit, not a later or rebased branch');
checkpoint.stages.forEach((stage,i) => {
    assert.equal(stage.id,'R'+String(i+1).padStart(2,'0'));
    assert.equal(stage.execution,'NOT_RUN');
    assert.equal(lineage[i],i===7 ? git('rev-parse','HEAD') : stage.commit);
    assert(git('show','-s','--format=%s',lineage[i]).startsWith(stage.id+' '),stage.id+' title');
    assert.equal(git('rev-parse',lineage[i]+'^'),i===0 ? checkpoint.baseline.commit : lineage[i-1]);
    assert(read(stage.checkpoint).includes('NOT_RUN'),stage.id+' execution label');
    assert.deepEqual(git('diff-tree','--no-commit-id','--name-only','-r',lineage[i]).split(/\r?\n/).sort(),
        [...stage.changedPaths].sort(),stage.id+' changed file list');
});
const d = 'src/DeploymentManager/';
const owned = read(d+'WizardForm.cs');
const close = section(owned,'private void RecordClosingState()','private static string SanitizeFileName');
const release = section(owned,'private void ReleaseOwnedResources()','private void RecordClosingState()');
const settings = read(d+'Services/AppDataService.cs');
const history = read(d+'Services/DeploymentHistoryService.cs');
const preview = read(d+'WizardForm.SourceAndTarget.cs');
const index = read(d+'Services/DeploymentHistoryIndex.cs');
const diag = read(d+'DiagnosticsCenterForm.cs');
const tests = read('src/DeploymentManager.SmokeTests/FinalRemediation08Tests.cs');
const checks = [
    ['state retention',close.includes('_appData.SaveState(_persistentState)') && close.includes('catch (Exception ex)')],
    ['resource disposal',ordered(release,'if (_ownedResourcesReleased) return;','_ownedResourcesReleased = true;',
        'CancelNavigationSourceProbe();','_healthMonitor.Dispose();','_logService.CloseSession();')],
    ['atomic settings and recovery',settings.includes('settings.Revision != current.Revision') &&
        settings.includes('PreserveAndResetCorruptSettings()') && settings.includes('backupPath: backup')],
    ['derived index',index.includes('".index-v2"') && index.includes('ContentHash') && index.includes('VerifyStable')],
    ['hosted separate from local and legacy display redacted',history.includes('IsRecordedHostedDeployment') &&
        history.includes('IsLocalTest') && history.includes('record.SessionLog = SecretRedactor.Redact(record.SessionLog)')],
    ['watch source and prevent stale navigation',preview.includes('changeStamp') && preview.includes('watcher.Error += OnError;') &&
        preview.includes('parentWatcher.Renamed += OnRootChange;') &&
        preview.includes('RunSourcePreflight(preserveIdentity, followWithEnvironment);') &&
        tail(owned,'private bool EnsureNavigationSourceReadiness()').includes('_sourcePresenterFresh?.Invoke() != true')],
    ['bounded diagnostics',diag.includes('GetHistoryPage(') && diag.includes('CancellationTokenSource')],
    ['owner suite',read('src/DeploymentManager.SmokeTests/Program.cs').includes('FinalRemediation08Tests.Run(testRoot);') &&
        ['OrderedWriter();','Settings();','History();','Health();','SourcePreview(root);'].every(unit => tests.includes(unit))]
];
for (const [label,yes] of checks) assert(yes,label);
for (const key of ['sourceScripts','compile','build','windowsSmoke','windowsManual','performanceBenchmark',
    'remoteOrNetworkFixture','iisSqlAcceptance','deployment','ci']) assert.equal(checkpoint.verification[key],'NOT_RUN',key);
console.log('V290-R exact lineage and '+checks.length+' static source guard groups completed. This is not Windows runtime acceptance.');
