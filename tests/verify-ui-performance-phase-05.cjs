const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const dir = path.join(__dirname,'..','src/DeploymentManager');
const read = p => fs.readFileSync(path.join(dir,p),'utf8');
const w = read('WizardForm.cs'), visual = read('WizardForm.VisualShell.cs'), theme = read('GwdUi.cs');
const reviewView = read('UI/Pages/ReviewStageView.cs');
const source = read('WizardForm.SourceAndTarget.cs'), review = read('WizardForm.ReviewAndLocation.cs'), infra = read('WizardForm.InstallersAndSql.cs');
const diag = read('DiagnosticsCenterForm.cs'), log = read('LogForm.cs'), history = read('DeploymentHistoryForm.cs');
const settings = read('SettingsForm.cs'), email = read('EmailNotificationSettingsForm.cs'), db = read('DatabaseRelocationForm.cs'), confirm = read('AutoYesConfirmationForm.cs');
const project = read('DeploymentManager.csproj'), program = read('Program.cs');
const { section: block, ordered } = require('./source-contract-r03.cjs');
const nav=read('WizardForm.Navigation.cs'), sourceView=read('UI/Pages/DeploymentSourceView.cs');
const shell = block(w,'private void BuildShell()','private async Task UpdateRuntimeMetricsAsync()');
const notice = block(visual,'private void NotifyUi(','private void EnqueueNotice(');
const queue = block(visual,'private void EnqueueNotice(','private void ActivateNotice(');
const clock = block(visual,'private void ActivateNotice(','private void UpdateNoticeHistory(');
const step = block(w,'private void UpdateStepIndicator()','private (bool Prepared, string Folder) NavigationSourceKey()');
const checks=[
 ['shared palette and fonts', ['Canvas','Surface','Ink','Accent','Border','Success','Warning','Danger','Body','Heading','Section'].every(x=>theme.includes(' '+x+' ='))],
 ['theme preserves controls and virtual grids', theme.includes('grid.AlternatingRowsDefaultCellStyle.BackColor')&&!theme.includes('grid.VirtualMode =')&&!theme.includes('button.Enabled =')&&!theme.includes('UseSystemPasswordChar')],
 ['high contrast fallback for shared theme',theme.includes('SystemInformation.HighContrast')&&ordered(theme,'public static void StyleTree(Control root)','if (root.IsDisposed) return;','if (HighContrast)','SystemColors.Window','foreach (Control child in root.Controls) StyleTree(child);','return;')],
 ['DPI configured once at startup',project.includes('<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>')&&program.includes('ApplicationConfiguration.Initialize()')&&!program.includes('Application.SetHighDpiMode(')],
 ['96 dpi form baseline',theme.includes('new SizeF(96F, 96F)')&&theme.includes('form.AutoScaleMode = AutoScaleMode.Dpi')],
 ['ordered registry retains all eight stage presenters',ordered(nav,...['BuildProjectSourcePage','BuildTargetPage','BuildScanPage','BuildIisPage','BuildInstallersPage','BuildSqlPage','BuildDeploymentLocationPage','BuildReviewPage'].map(x=>'static wizard => wizard.'+x+'()'))&&nav.includes('Stages[_pageIndex].Render(this)')],
 ['keyboard step navigation',shell.includes('var label = new Button')&&shell.includes('AccessibleName = step, TabStop = true')&&shell.includes('if (CanNavigateInteractively()) ShowPage(stepIndex)')],
 ['prior position is not PASS',step.includes('i < _pageIndex ? "Earlier" : "Pending"')&&!step.includes('label.Text = $"✓')],
 ['dashboard truthful inputs',visual.includes('_profile.ApplicationName')&&visual.includes('_profile.BuildMode')&&visual.includes('_operationRunning ? "Operation running"')&&shell.includes('BuildDashboard()')],
 ['dashboard and health remain independent of deployment result',w.includes('live release identity not verified here')&&w.includes('A localhost HTTP response does NOT verify IIS site ownership')],
 ['footer responsive layout',shell.includes('RowCount = 4')&&shell.includes('WrapContents = true')&&shell.includes('footer.Controls.Add(footerButtons, 0, 3)')&&shell.includes('Wizard position (not deployment completion)')],
 ['all old action menu commands present', ['Save Profile','Load Profile','Settings','Database Management','Clean Previous Versions','Deployment Cleanup...','Lock Source','Export Clean Source ZIP','Diagnostics Center','Email Notifications...'].every(x=>shell.includes('"'+x+'"'))],
 ['new view menu shortcuts',shell.includes('Keys.Control | Keys.L')&&shell.includes('Keys.Control | Keys.Shift | Keys.H')&&shell.includes('Keys.Control | Keys.Shift | Keys.N')],
 ['modeless live log retains Cancel Deploy',visual.includes('viewer.Show(this)')&&shell.includes('_autoDeployCts?.Cancel()')&&w.includes('RequestDeploymentCancellation()')],
 ['notifications bounded, redacted',notice.includes('SecretRedactor.Redact(message)')&&notice.includes('if (_notices.Count > MaxNotices) _notices.RemoveAt(0);')&&ordered(queue,'_pendingNotices.Count >= MaxNotices','if ((int)notice.Kind <= lowest) return','_pendingNotices.RemoveAt','_pendingNotices.Add(notice)')],
 ['warnings not overwritten by information',ordered(notice,'_activeNotice is null','else if ((int)kind > (int)_activeNotice.Kind)','EnqueueNotice(_activeNotice)','ActivateNotice(notice)','else EnqueueNotice(notice)')&&visual.includes('UiNoticeKind.Critical')],
 ['only info/success count down',clock.includes('notice.Kind is UiNoticeKind.Info or UiNoticeKind.Success ? Stopwatch.GetTimestamp() : null')&&visual.includes('Stopwatch.GetElapsedTime(stamp).TotalSeconds')&&visual.includes('if (remaining is <= 0) { DismissNotice(); return; }')],
 ['dismiss preserves history and recovery',ordered(block(visual,'private void DismissNotice()','private string NotificationText()'),'OrderByDescending(x => x.Kind).ThenBy(x => x.Sequence)','_pendingNotices.Remove(next)','ActivateNotice(next)')&&!/(_notices|_pendingNotices)\.Clear\(/.test(block(visual,'private void DismissNotice()','private string NotificationText()'))&&w.includes('RECOVERY REVIEW REQUIRED')],
 ['local build outcome distinct',visual.includes('Local build passed; hosted IIS is unchanged.')],
 ['notification resources closed',visual.includes('_noticeTimer.Stop(); _noticeTimer.Dispose()')&&w.includes('StopVisualResources();')],
 ['profile controls mounted and guarded',sourceView.includes('profileActions.Controls.AddRange(new Control[] { SaveProfile, LoadProfile, LockSource, ExportSource })')&&sourceView.includes('AddRow(form, "Profile actions", profileActions)')&&source.includes('if (!CanNavigateInteractively()) return;')],
 ['source lock persists across view reconstruction',sourceView.includes('Checked = sourceLocked')&&source.includes('_sourceLocked = lockSource.Checked')&&w.includes('lockSourceMenu.Checked = _sourceLocked')],
 ['review mode summary updates',reviewView.includes('mode.SelectedIndexChanged +=')&&reviewView.includes('summary.Text = actions.ChangeBuildMode(mode.SelectedIndex)')&&review.includes('InvalidateEnvironmentEvidence();')&&review.includes('return DescribeReview();')],
 ['review view releases callbacks and preserves shared log',reviewView.includes('ReviewStageView : UserControl')&&reviewView.includes('Deactivate() => _actions = null')&&reviewView.includes('parent.Controls.Remove(_sharedLog)')&&nav.includes('_reviewView?.Deactivate()')],
 ['review splitter initialized once',reviewView.includes('if (initialized || split.IsDisposed')&&reviewView.includes('TrackSplitRatio(Math.Clamp(')&&review.includes('ratio => _reviewSplitRatio = ratio')],
 ['major dialogs use shared styling',[diag,log,history,settings,email,db,confirm].every(s=>s.includes('GwdUi.ConfigureForm(this)')&&s.includes('GwdUi.StyleTree(this)'))],
 ['lazy tab and installer styles applied',diag.includes('GwdUi.StyleTree(created)')&&infra.includes('GwdUi.StyleTree(box)')],
 ['countdown and affirmative protection semantics retained',confirm.includes('AcceptButton = _yes;')&&confirm.includes('CancelButton = _no;')&&confirm.includes('if (_remainingSeconds <= 0) Finish(DialogResult.Yes)')],
 ['destructive database action still disabled initially',db.includes('Text = "Detach, Move && Attach", Width = 210, Enabled = false')&&db.includes('GwdUi.ButtonStyle(_move, danger: true)')]
];
for(const [name,ok] of checks){assert(ok,name);console.log('PASS: '+name);}
console.log(checks.length+' phase 05 static source checks; Windows visual and runtime acceptance pending.');
