// For the owner to run in the candidate checkout; not executed in this mission.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const {verifyPreservation} = require('./source-contract-r03.cjs');
const assert = require('node:assert/strict');
const root = path.join(__dirname, '..');
const read = name => fs.readFileSync(path.join(root,name),'utf8').replace(/\r\n/g,'\n');
const app = 'src/DeploymentManager/';
const check = (file,...anchors) => {
  const text = read(app+file);
  for (const anchor of anchors) assert(text.includes(anchor),file+' missing '+anchor);
};
const current=verifyPreservation();
const version=JSON.parse(read('deployment-manager.version.json')).version;
assert(read('Directory.Build.props').includes('<DeploymentManagerVersion>'+version+'</DeploymentManagerVersion>'));
const ck=JSON.parse(read('audit/regression/final-architecture-07-checkpoint.json'));
// Immutable historical evidence, not the V290 release stamp.
assert.equal(ck.candidate,current.baseline.release);
assert.equal(ck.build,'NOT_RUN_POLICY');
assert.equal(ck.tests,'NOT_RUN_POLICY');
for(const name of ['DeploymentSource','DeploymentTarget','EnvironmentScan','IisManagement','InstallerStage','SqlConfiguration','DeploymentLocation','ReviewStage'])
  check('UI/Pages/'+name+'View.cs',name+'View : UserControl');
check('WizardForm.Navigation.cs','Stages[_pageIndex].Render(this)','FlushDeferredSettingsSaves();','_log.Parent.Controls.Remove(_log)');
check('WizardForm.Settings.cs','OrderBy(entry => entry.Value.Order)','_settingsWriter.Enqueue','TryDrain');
check('Services/AppDataService.cs','RemoteCredentialGeneration != generation','WithSettingsWriter','output.Flush(flushToDisk: true)');
check('Services/DeploymentHistoryIndex.cs','GetCurrentHash()','VerifyStable','ReadSidecar','ReadRow');
check('DeploymentHistoryForm.cs','GetHistoryPage(','HistoryPageSize');
check('SourcePreviewInspector.cs','_sourcePreviewSlots','environment.DetectApiProject(folder)','Task.WhenAny');
check('WizardForm.SourceAndTarget.cs','generation == sourcePreflightGeneration','result.CanWatch','SaveRememberedRemoteCredential(currentGeneration,');
check('WizardForm.cs','ValidatePreparedPublish(','_healthMonitor.IsCurrent(snapshot)','VerifyRunningApiReleaseAsync(','VerifyRunningFrontendReleaseAsync(','RECOVERY REVIEW REQUIRED');
check('Services/BackgroundHealthMonitor.cs','SnapshotForHealth()','_inFlightGeneration != _generation','generation == _generation','AllowAutoRedirect = false');
check('DeploymentManager.csproj','PerMonitorV2');
check('GwdUi.cs','ResponsiveLayout.FitWorkingArea','GotFocus','HighContrast','ShowFeedback');
check('WizardForm.Dashboard.cs','ReadDashboardHistory','Check configured health','window.Show(this)','Not Verified by this dashboard');
check('WizardForm.VisualShell.cs','UiNoticeKind.Critical','duplicate.Repetitions','Stopwatch.GetElapsedTime','ThenBy(x => x.Sequence)');
console.log('Source structure only; no runtime, database or deployment action.');
