const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const read = p => fs.readFileSync(path.join(__dirname,'..',p),'utf8');
const dir = 'src/DeploymentManager/';
const core = read(dir+'WizardForm.cs');
const source = read(dir+'WizardForm.SourceAndTarget.cs');
const infra = read(dir+'WizardForm.InstallersAndSql.cs');
const review = read(dir+'WizardForm.ReviewAndLocation.cs');
const { section, tail, ordered } = require('./source-contract-r03.cjs');
const navigation = read(dir+'WizardForm.Navigation.cs');
const iis = read(dir+'WizardForm.Iis.cs');
const card = read(dir+'UI/Controls/StatusCardUi.cs');
const nav = section(navigation,'private void NavigateToPage(','private void DisposeContentControls()','WizardForm.Navigation.cs');
const dispose = tail(navigation,'private void DisposeContentControls()','WizardForm.Navigation.cs');
const scanView = read(dir+'UI/Pages/EnvironmentScanView.cs');
const scanPage = section(core,'private void BuildScanPage()','private Task ScanEnvironmentAsyncWithoutToken');
const scan = section(core,'private async Task ScanEnvironmentAsync(','private static string NormalizeAutoHostingRoot');
const busy = section(core,'private async Task BusyAsync(','private static FlowLayoutPanel VerticalPanel');
const checks = [
 ['scan view lifecycle', scanView.includes('EnvironmentScanView : UserControl')&&scanView.includes('_scan.Click -= OnScanClick')&&dispose.includes('_scanView?.Deactivate()')&&scanPage.includes('ReferenceEquals(scan, _scanView)')],
 ['partial ownership and all eight page controls', [core,source,infra,review,navigation,iis].every(s=>s.includes('public sealed partial class WizardForm'))&&['DeploymentSource','DeploymentTarget','EnvironmentScan','IisManagement','InstallerStage','SqlConfiguration','DeploymentLocation','ReviewStage'].every(x=>read(dir+'UI/Pages/'+x+'View.cs').includes(x+'View : UserControl'))],
 ['source target views separated', source.includes('private void BuildProjectSourcePage()')&&source.includes('private void BuildTargetPage()')&&!core.includes('private void BuildProjectSourcePage()')],
 ['installer SQL views separated', infra.includes('private void BuildInstallersPage()')&&infra.includes('private void BuildSqlPage()')&&!core.includes('private void BuildSqlPage()')],
 ['location review views separated', review.includes('private void BuildReviewPage()')&&review.includes('private void BuildDeploymentLocationPage()')&&!core.includes('private void BuildReviewPage()')],
 ['same-page short circuit', nav.includes('!forceRebuild && _renderedPageIndex == requested')&&ordered(nav,'!forceRebuild && _renderedPageIndex == requested','UpdateNavigationState();','return;','DisposeContentControls();')],
 ['explicit forced rebuild', navigation.includes('NavigateToPage(_pageIndex, forceRebuild: true)')&&infra.includes('if (_pageIndex == 4) RebuildCurrentPage();')],
 ['page index and layout order', ordered(nav,'_content.SuspendLayout()','DisposeContentControls();','_pageIndex = requested;','Stages[_pageIndex].Render(this)','_content.ResumeLayout(true)')&&nav.includes('_content.SuspendLayout()')&&nav.includes('_content.ResumeLayout(true)')],
 ['dispose resets render identity and preserves shared log', dispose.includes('_renderedPageIndex = -1')&&dispose.includes('_log.Parent.Controls.Remove(_log)')&&ordered(dispose,'FlushDeferredSettingsSaves();','_log.Parent.Controls.Remove(_log)','control.Dispose()')],
 ['interactive reentrancy guard', navigation.includes('!_dependencySyncRunning && !_navigationInProgress && !_pageActionRunning')&&core.includes('_navigationInProgress = true;')&&busy.includes('_pageActionRunning = true;')&&!busy.includes('_operationRunning = false;')],
 ['auto deploy does not schedule duplicate scan', scanPage.includes('if (!_autoDeployRunning)')&&scanPage.includes('scan.RequestScan();')&&scanView.includes('_scan.PerformClick();')&&core.includes('await ScanEnvironmentAsync(ct);')],
 ['scan publishes only after cancellation and input checks', ordered(scan,'var sqlInstances = await _sql.DetectInstancesAsync(ct)','ct.ThrowIfCancellationRequested()','mode != _profile.BuildMode','_iisState = iisState')&&scan.includes('mode != _profile.BuildMode')&&scan.includes('ct.ThrowIfCancellationRequested()')],
 ['background request cancellation on teardown', dispose.includes('CancelBackgroundVerification()')&&source.includes('request.Dispose();')&&core.includes('await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct)')],
 ['status cards updated in place', scanView.includes('UpdateStatusCard(_cardViews[i],')&&scan.includes('_scanView?.RefreshCards(GetScanCards())')&&iis.includes('UpdateStatusCard(status, "IIS status"')&&card.includes('internal static void UpdateStatusCard')&&card.includes('if (card.IsDisposed) return;')&&!scanPage.includes('ReplaceCard(')],
 ['installer cards disposed and scroll preserved', infra.includes('foreach (var card in previous) card.Dispose();')&&infra.includes('list.AutoScrollPosition = new Point(-oldScroll.X, -oldScroll.Y);')],
 ['Start Over avoids forced GC', !core.includes('GC.Collect(GC.MaxGeneration')],
 ['valid source reentry preserves identity', source.includes('RunSourcePreflight(bool preserveIdentity = true')&&['app','site','database'].every(key=>new RegExp(key+'\\.Text == expected\\.[A-Za-z]+ && \\(string\\.IsNullOrWhiteSpace\\('+key+'\\.Text\\) \\|\\| \\(!preserveIdentity && !prepared\\)\\)').test(source))]
];
for(const [name,ok] of checks){assert(ok,name);console.log('PASS: '+name);}
console.log(checks.length+' phase 03 source guards passed. No live IIS, SQL or Windows UI test.');
