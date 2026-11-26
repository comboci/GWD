const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const read = p => fs.readFileSync(path.join(__dirname,'..',p),'utf8');
const dir = 'src/DeploymentManager/';
const core = read(dir+'WizardForm.cs');
const source = read(dir+'WizardForm.SourceAndTarget.cs');
const infra = read(dir+'WizardForm.InstallersAndSql.cs');
const review = read(dir+'WizardForm.ReviewAndLocation.cs');
const section = (s,a,b) => { const i=s.indexOf(a), j=s.indexOf(b,i+a.length); assert(i>=0&&j>i,a); return s.slice(i,j); };
const nav = section(core,'private void NavigateToPage(','private void QueueDeferredSettingsSave');
const dispose = section(core,'private void DisposeContentControls()','private void UpdateStepIndicator()');
const scanView = read(dir+'UI/Pages/EnvironmentScanView.cs');
const scanPage = section(core,'private void BuildScanPage()','private Task ScanEnvironmentAsyncWithoutToken');
const scan = section(core,'private async Task ScanEnvironmentAsync(','private void BuildIisPage()');
const busy = section(core,'private async Task BusyAsync(','private static FlowLayoutPanel VerticalPanel');
const checks = [
 ['scan view lifecycle', scanView.includes('EnvironmentScanView : UserControl')&&scanView.includes('_scan.Click -= OnScanClick')&&dispose.includes('_scanView?.Deactivate()')&&scanPage.includes('ReferenceEquals(scan, _scanView)')],
 ['four partial classes', [core,source,infra,review].every(s=>s.includes('public sealed partial class WizardForm'))],
 ['source target views separated', source.includes('private void BuildProjectSourcePage()')&&source.includes('private void BuildTargetPage()')&&!core.includes('private void BuildProjectSourcePage()')],
 ['installer SQL views separated', infra.includes('private void BuildInstallersPage()')&&infra.includes('private void BuildSqlPage()')&&!core.includes('private void BuildSqlPage()')],
 ['location review views separated', review.includes('private void BuildReviewPage()')&&review.includes('private void BuildDeploymentLocationPage()')&&!core.includes('private void BuildReviewPage()')],
 ['same-page short circuit', nav.includes('!forceRebuild && _renderedPageIndex == requested')&&nav.indexOf('return;')<nav.indexOf('DisposeContentControls();')],
 ['explicit forced rebuild', core.includes('NavigateToPage(_pageIndex, forceRebuild: true)')&&infra.includes('if (_pageIndex == 4) RebuildCurrentPage();')],
 ['page index and layout order', nav.indexOf('DisposeContentControls();')<nav.indexOf('_pageIndex = requested;')&&nav.includes('_content.SuspendLayout()')&&nav.includes('_content.ResumeLayout(true)')],
 ['dispose resets render identity and preserves shared log', dispose.includes('_renderedPageIndex = -1')&&dispose.includes('_log.Parent.Controls.Remove(_log)')&&dispose.indexOf('_log.Parent.Controls.Remove(_log)')<dispose.indexOf('control.Dispose()')],
 ['interactive reentrancy guard', core.includes('!_dependencySyncRunning && !_navigationInProgress && !_pageActionRunning')&&core.includes('_navigationInProgress = true;')&&busy.includes('_pageActionRunning = true;')&&!busy.includes('_operationRunning = false;')],
 ['auto deploy does not schedule duplicate scan', scanPage.includes('if (!_autoDeployRunning)')&&scanPage.includes('scan.RequestScan();')&&scanView.includes('_scan.PerformClick();')&&core.includes('await ScanEnvironmentAsync(ct);')],
 ['scan publishes only after cancellation and input checks', scan.indexOf('var sqlInstances = await _sql.DetectInstancesAsync(ct)')<scan.indexOf('_iisState = iisState')&&scan.includes('mode != _profile.BuildMode')&&scan.includes('ct.ThrowIfCancellationRequested()')],
 ['background request cancellation on teardown', dispose.includes('CancelBackgroundVerification()')&&source.includes('request.Dispose();')&&core.includes('await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct)')],
 ['status cards updated in place', scanView.includes('UpdateStatusCard(_cardViews[i],')&&scan.includes('_scanView?.RefreshCards(GetScanCards())')&&core.includes('UpdateStatusCard(status, "IIS status"')&&!scanPage.includes('ReplaceCard(')],
 ['installer cards disposed and scroll preserved', infra.includes('foreach (var card in previous) card.Dispose();')&&infra.includes('list.AutoScrollPosition = new Point(-oldScroll.X, -oldScroll.Y);')],
 ['Start Over avoids forced GC', !core.includes('GC.Collect(GC.MaxGeneration')],
 ['valid source reentry preserves identity', source.includes('Detect(preserveIdentity: true)')&&source.includes('preserveIdentity && !string.IsNullOrWhiteSpace(_profile.ApplicationName)')]
];
for(const [name,ok] of checks){assert(ok,name);console.log('PASS: '+name);}
console.log(checks.length+' phase 03 source guards passed. No live IIS, SQL or Windows UI test.');
