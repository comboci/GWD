const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const dir = path.join(__dirname,'..','src/DeploymentManager');
const read = p => fs.readFileSync(path.join(dir,p),'utf8').replace(/\r\n/g,'\n');
const w=read('WizardForm.cs'), s=read('WizardForm.SourceAndTarget.cs'), ins=read('WizardForm.InstallersAndSql.cs'), rv=read('WizardForm.ReviewAndLocation.cs');
const engine=read('Services/DeploymentService.cs'), env=read('Services/EnvironmentService.cs'), history=read('Services/DeploymentHistoryService.cs'), reader=read('Services/LogPreviewReader.cs');
const { section, ordered } = require('./source-contract-r03.cjs');
const sourceView=read('UI/Pages/DeploymentSourceView.cs');
const nav=section(w,'private void UpdateNavigationState()','private bool CanLeaveTargetPage()');
const auto=section(w,'private async Task AutoDeployAsync()','private static bool IsLikelyOfflineDependencyFailure');
const next=section(w,'private async Task NextAsync()','private void BuildWelcomePage()');
const scan=section(w,'private async Task ScanEnvironmentAsync(','private static string NormalizeAutoHostingRoot');
const deploy=section(w,'private async Task DeployAsync()','private async Task NotifyDeploymentOutcomeAsync');
const activate=section(engine,'public async Task<string> ActivateStagingAsync','private async Task RestoreHostedFolderSafelyAsync');
const recovery=section(engine,'public async Task CleanupFailedDeploymentAsync','public void MarkDeploymentSuccessful');
const wait=section(engine,'private async Task WaitForAppPoolStoppedAsync','private async Task DeleteDirectoryWithRetryAsync');
const checks=[
['source cache no recursive validation in navigation',!/DetectApiProject\(|ValidatePreparedPublish\(|Directory\.(Exists|EnumerateFiles)/.test(nav)],
['page inputs frozen while busy',nav.includes('_content.Enabled = canNavigate;')&&nav.includes('CanNavigateInteractively()')],
['prepared identity preserved',sourceView.includes('Text = applicationName')&&s.includes('Source project not required in Prepared IIS Publish mode.')&&s.includes('app.Text == expected.App && (string.IsNullOrWhiteSpace(app.Text) || (!preserveIdentity && !prepared))')&&s.includes('site.Text == expected.Site')&&s.includes('database.Text == expected.Db')],
['manual recheck',s.includes('recheckSource.Click')&&ordered(section(s,'recheckSource.Click','dependencyMode.SelectedIndexChanged','manual source recheck'),'InvalidateNavigationSourceReadiness();','InvalidateEnvironmentEvidence();','RunSourcePreflight();')&&s.includes('_ = RunSourceTestsAsync();')],
['profile import snapshot avoids TextChanged overwrite',ordered(s,'var sqlIdentityChanged =','dto.ApplyTo(_profile);','_sourceLocked = dto.SourceLocked;','RebuildCurrentPage();')],
['credentials not reused after SQL identity change',ordered(s,'if (sqlIdentityChanged)','_profile.SqlPassword = string.Empty;','_profile.SqlCredentialConfigured = false;')],
['dependency finalizers preserve result',section(s,'offlineAction.Click','offlineTest.Click').includes('_activity.Text = _offlineDependenciesReady ?')&&!section(s,'offlineAction.Click','offlineTest.Click').includes('ApplyDependencyMode();')&&!section(s,'offlineTest.Click','// Start only with a WinForms message pump').includes('finally { ApplyDependencyMode(); }')],
['manual and automatic final target validation',ordered(next,'case 7:','ValidateTargetPage();','ValidateIisSitePage();','await DeployAsync();')&&ordered(auto,'case 7:','ValidateTargetPage();','ct.ThrowIfCancellationRequested();','ValidateIisSitePage();','await DeployAsync();')],
['automatic source validation',ordered(auto,'case 0:','ValidatePreparedPublish','DetectApiProject','case 1:')],
['automatic SQL cancellation',auto.includes('await _sql.TestConnectionWithSafeFallbackAsync(_profile, ct);')],
['automatic deployment cancellation forwarded',ordered(w,'_autoDeploy.Click +=','if (_autoDeployRunning)','if (_operationRunning && _pageIndex == _steps.Length - 1)','RequestDeploymentCancellation();','_autoDeployCts?.Cancel();')],
['manual local build skips IIS and database requirements',next.includes('case 3:\n                    if (string.Equals(_profile.BuildMode, "Fast Local Test"')&&next.includes('case 5:\n                    if (string.Equals(_profile.BuildMode, "Fast Local Test"')&&next.includes('(!localOnly && !_hostingState.Installed)')],
['environment evidence reset before scan',ordered(scan,'_scanCompleted = false;','ValidateTargetPage();','var sqlInstances = await','_scanCompleted = true;')],
['scan verifies target identity',scan.includes('targetMode != _profile.TargetMode || targetHost != _profile.TargetHost')],
['review mode invalidates scan',rv.includes('InvalidateEnvironmentEvidence();')],
['remote readiness serialized',s.includes('await BusyAsync(testRemote, async () =>')],
['automatic SQL probe serialized',ins.includes('await BusyAsync(test, async () =>')&&ins.includes('!CanNavigateInteractively() || string.IsNullOrWhiteSpace(_profile.SqlInstance)')],
['IIS fallback errors not empty',env.includes('IIS site inventory failed or exceeded the output limit; an empty list is not evidence.')&&env.includes('IIS application pool inventory failed or exceeded the output limit; an empty list is not evidence.')],
['malformed history isolated',history.includes('Invalid history mode, result or duration.')&&history.includes('Invalid build performance history.')&&history.includes('catch (JsonException ex)')],
['original log protected against same destination',ordered(reader,'Path.GetFullPath(source), Path.GetFullPath(destination)','throw new IOException("Log snapshot destination must differ','var temporary =')],
['IIS preflight before backup and STOP before copy',ordered(activate,'ValidateIisPreflightAsync','CreateUniqueRollbackFolder','await StopSiteForDeploymentAsync','CopyDirectory(stagingFolder')],
['IIS STOP rechecked before swap',ordered(activate,'CopyDirectory(stagingFolder','await WaitForAppPoolStoppedAsync(profile, ct);','Directory.Move(fullTarget')],
['engine never deletes or recreates IIS site',!engine.includes('delete site ')&&!engine.includes('add site ')&&!engine.includes('iisreset')&&!engine.includes('Restart-Service')],
['IIS STOP fail-closed',wait.includes('site != "Stopped" || pool != "Stopped"')&&wait.includes('throw new IOException')],
['no false HTTP claim on unchanged files',ordered(recovery,'if (!_filesChanged)','await RestoreOriginalIisStatesAsync(profile);','FILES_UNCHANGED / ORIGINAL_IIS_STATES_VERIFIED / HTTP_NOT_CHECKED')],
['rollback health evidence before healthy',ordered(recovery,'FILES_RESTORED / IIS_UNHEALTHY','try { await RestoreOriginalIisStatesAsync(profile); }','var recoveredHealth = await HealthCheckAsync(profile, recoveryToken)','FILES_RESTORED / IIS_HEALTHY')],
['engine lease before build',ordered(deploy,'BeginIisAttempt','PublishAsync(_profile')&&deploy.includes('iisAttempt?.Dispose()')]
];
for(const [name,ok] of checks){assert(ok,name);console.log('PASS: '+name);}
console.log(checks.length+' V269-R source guards; not build or runtime evidence.');
