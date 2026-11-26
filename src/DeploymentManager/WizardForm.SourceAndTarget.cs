using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private void BuildProjectSourcePage()
    {
        _pageTitle.Text = "Select source and dependency strategy";
        _pageDescription.Text = "Choose the incoming source and all build/dependency settings here. Source preflight runs automatically. Persistent dependencies and stable build workspaces stay outside the versioned source folder.";
        var form = FormGrid();
        var source = new TextBox { Text = _profile.SourceFolder, Dock = DockStyle.Fill, ReadOnly = true, TabStop = false, BackColor = SystemColors.Window };
        var browse = new Button { Text = "Browse source...", AutoSize = true };
        var lockSource = new CheckBox { Text = "Lock source", AutoSize = true, Checked = false, Margin = new Padding(8, 7, 3, 3) };
        var saveProfile = new Button { Text = "Save Profile", AutoSize = true };
        var loadProfile = new Button { Text = "Load Profile", AutoSize = true };
        _saveProfileAction = saveProfile;
        _loadProfileAction = loadProfile;
        var exportSource = new Button { Text = "Export Clean Source ZIP", AutoSize = true };
        _lockSourceAction = lockSource;
        _exportSourceAction = exportSource;
        // The quick actions and menu commands invoke the same live controls.
        // Parenting them also gives them normal focus and disposal lifetimes.
        foreach (var action in new Control[] { saveProfile, loadProfile, lockSource, exportSource })
            action.Margin = new Padding(2, 1, 3, 3);
        var profileActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        profileActions.Controls.AddRange(new Control[] { saveProfile, loadProfile, lockSource, exportSource });
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(source, 0, 0); sourceRow.Controls.Add(browse, 1, 0);

        var detectedRoot = new Label { AutoSize = true, MaximumSize = new Size(850, 0), ForeColor = Color.DimGray };
        var app = new TextBox { Width = 260 };
        var site = new TextBox { Width = 260 };
        var database = new TextBox { Width = 260 };
        var buildMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        buildMode.Items.AddRange(new object[] { "Fast Local Test", "Fast IIS Update", "Full IIS Deploy", "Visual Studio prepared", "Prepared IIS Publish" });
        buildMode.SelectedItem = buildMode.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), _profile.BuildMode, StringComparison.OrdinalIgnoreCase)) ?? buildMode.Items[0];
        var dependencyMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        dependencyMode.Items.AddRange(new object[] { "Offline Only", "Hybrid", "Online" });
        dependencyMode.SelectedItem = NormalizeDependencyMode(_profile.DependencyResolutionMode);
        var modeMeaning = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DarkOrange };
        var preparedPublish = new TextBox { Text = _profile.ReadyPublishFolder, Dock = DockStyle.Fill };
        var browsePreparedPublish = new Button { Text = "Browse prepared publish...", AutoSize = true };
        var preparedPublishRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        preparedPublishRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); preparedPublishRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        preparedPublishRow.Controls.Add(preparedPublish, 0, 0); preparedPublishRow.Controls.Add(browsePreparedPublish, 1, 0);
        var preparedValidation = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Text = "Select Prepared IIS Publish mode to validate a prebuilt IIS artifact." };

        var repository = new TextBox { Text = _profile.DeploymentRepositoryFolder, Dock = DockStyle.Fill };
        var browseRepository = new Button { Text = "Browse repository...", AutoSize = true };
        var repositoryRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        repositoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); repositoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        repositoryRow.Controls.Add(repository, 0, 0); repositoryRow.Controls.Add(browseRepository, 1, 0);

        var workspace = new TextBox { Text = _profile.WorkspaceRoot, Dock = DockStyle.Fill };
        var browseWorkspace = new Button { Text = "Browse workspace...", AutoSize = true };
        var workspaceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        workspaceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); workspaceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        workspaceRow.Controls.Add(workspace, 0, 0); workspaceRow.Controls.Add(browseWorkspace, 1, 0);

        var offlineAction = new Button { Text = "Sync Missing Online Once", AutoSize = true };
        var offlineTest = new Button { Text = "Test Offline Build", AutoSize = true };
        var offlineState = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Dependency readiness has not been checked yet." };
        var dependencySyncProgress = new ProgressBar { Width = 360, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Visible = false };
        var dependencySyncActivity = new Label { AutoSize = true, ForeColor = Color.RoyalBlue, MaximumSize = new Size(800, 0), Visible = false, Text = "Checking dependencies..." };

        AddRow(form, "Profile actions", profileActions);
        AddRow(form, "Source folder", sourceRow);

        AddRow(form, "Detected source root", detectedRoot);
        AddRow(form, "Application name", app);
        AddRow(form, "IIS site name", site);
        AddRow(form, "Database name", database);
        AddRow(form, "Build / deployment mode", buildMode);
        AddRow(form, "Dependency mode", dependencyMode);
        AddRow(form, "Selected behavior", modeMeaning);
        AddRow(form, "Prepared publish folder", preparedPublishRow);
        AddRow(form, "Prepared publish validation", preparedValidation);
        AddRow(form, "Shared Dependency Repository", repositoryRow);
        AddRow(form, "Repository behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Persistent across source versions. NuGet/npm/installers/offline packages live here; the incoming source folder is never used as the dependency repository." });
        AddRow(form, "Stable Build Workspace", workspaceRow);
        AddRow(form, "Workspace behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(800, 0), Text = "Persistent build working area. Versioned incoming sources may be deleted after deployment without losing reusable dependency/build state." });
        AddRow(form, "Dependency actions", Wrap(offlineAction, offlineTest));
        AddRow(form, "Dependency status", offlineState);
        AddRow(form, "Dependency sync", Wrap(dependencySyncProgress, dependencySyncActivity));
        var sourceTestResult = new Label { AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DimGray, Text = "Automatic source preflight starts as soon as a valid source folder is selected." };
        AddRow(form, "Automatic source preflight", sourceTestResult);
        _content.Controls.Add(form);

        void RefreshPreparedPublishValidation()
        {
            var prepared = string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase);
            preparedPublish.Enabled = browsePreparedPublish.Enabled = prepared;
            repository.Enabled = browseRepository.Enabled = workspace.Enabled = browseWorkspace.Enabled = dependencyMode.Enabled = !prepared;
            if (!prepared) { preparedValidation.Text = "Not applicable for source-build mode."; preparedValidation.ForeColor = Color.DimGray; return; }
            _profile.ReadyPublishFolder = preparedPublish.Text.Trim();
            var validation = DeploymentService.ValidatePreparedPublish(_profile.ReadyPublishFolder);
            RememberNavigationSourceReadiness(true, _profile.ReadyPublishFolder, validation.Ok);
            preparedValidation.Text = validation.Detail;
            preparedValidation.ForeColor = validation.Ok ? Color.ForestGreen : Color.Firebrick;
            if (validation.Ok && string.IsNullOrWhiteSpace(app.Text) && !string.IsNullOrWhiteSpace(validation.ApplicationBinary))
            {
                var inferred = Path.GetFileNameWithoutExtension(validation.ApplicationBinary);
                if (inferred.EndsWith(".Api", StringComparison.OrdinalIgnoreCase)) inferred = inferred[..^4];
                app.Text = inferred;
                var safe = new string(inferred.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrWhiteSpace(site.Text)) site.Text = safe;
                if (string.IsNullOrWhiteSpace(database.Text)) database.Text = safe;
            }
            _profile.RequiresNode = false;
        }

        void Detect(bool preserveIdentity = false)
        {
            _profile.SourceFolder = source.Text.Trim();
            var api = Directory.Exists(_profile.SourceFolder) ? _environment.DetectApiProject(_profile.SourceFolder) : null;
            RememberNavigationSourceReadiness(false, _profile.SourceFolder, !string.IsNullOrWhiteSpace(api));
            if (api is null)
            {
                _profile.ApiProjectPath = string.Empty;
                detectedRoot.Text = "No ASP.NET Core Web project detected."; detectedRoot.ForeColor = Color.Firebrick;
                app.Text = site.Text = database.Text = string.Empty;
            }
            else
            {
                _profile.ApiProjectPath = api;
                var root = DeploymentService.ResolveCanonicalSourceRoot(_profile.SourceFolder, api);
                detectedRoot.Text = root + "\r\nWeb project: " + api; detectedRoot.ForeColor = Color.ForestGreen;
                var inferred = InferApplicationName(_profile.SourceFolder, api);
                app.Text = preserveIdentity && !string.IsNullOrWhiteSpace(_profile.ApplicationName) ? _profile.ApplicationName : inferred;
                var safe = new string(inferred.Where(char.IsLetterOrDigit).ToArray());
                site.Text = preserveIdentity && !string.IsNullOrWhiteSpace(_profile.SiteName) ? _profile.SiteName : safe;
                database.Text = preserveIdentity && !string.IsNullOrWhiteSpace(_profile.DatabaseName) ? _profile.DatabaseName : safe;
            }
            UpdateNavigationState();
        }

        static bool IsGeneratedFrontendCandidate(string root, string file)
        {
            var rel = "/" + Path.GetRelativePath(root, file).Replace('\\','/').Trim('/') + "/";
            string[] excluded = { "/node_modules/", "/bin/", "/obj/", "/dist/", "/build/", "/out/", "/.vite/", "/iis-publish/", "/publish/" };
            return excluded.Any(x => rel.Contains(x, StringComparison.OrdinalIgnoreCase));
        }

        // Only one source preflight may publish results for the current page.
        // Cancellation does not interrupt every filesystem API, but it prevents stale
        // results from being applied after a source/mode change or page disposal.
        CancellationTokenSource? sourcePreflightCts = null;
        var sourcePreflightGeneration = 0;

        void CancelSourcePreflight()
        {
            sourcePreflightGeneration++;
            sourcePreflightCts?.Cancel();
        }

        sourceTestResult.Disposed += (_, _) => CancelSourcePreflight();

        void RunSourceTests()
        {
            CancelSourcePreflight();
            var request = new CancellationTokenSource();
            sourcePreflightCts = request;
            var generation = sourcePreflightGeneration;
            var selectedMode = _profile.BuildMode;
            var selectedSource = source.Text.Trim();
            var selectedPrepared = preparedPublish.Text.Trim();
            sourceTestResult.Text = "Running source preflight tests...";
            sourceTestResult.ForeColor = Color.RoyalBlue;
            _ = RunSourceTestsAsync(request, generation, selectedMode, selectedSource, selectedPrepared);
        }

        async Task RunSourceTestsAsync(CancellationTokenSource request, int generation, string mode, string selectedSource, string selectedPrepared)
        {
            try
            {
                // Coalesce consecutive programmatic TextChanged events and large-folder selections.
                await Task.Delay(200, request.Token);
                // Worker code never reads or writes WinForms controls or mutable profile fields.
                var result = await Task.Run(
                    () => InspectSourcePreflight(_environment, mode, selectedSource, selectedPrepared, request.Token),
                    request.Token);
                if (request.IsCancellationRequested || generation != sourcePreflightGeneration ||
                    IsDisposed || Disposing || sourceTestResult.IsDisposed || _pageIndex != 0 ||
                    !string.Equals(mode, _profile.BuildMode, StringComparison.Ordinal) ||
                    !string.Equals(selectedSource, source.Text.Trim(), StringComparison.Ordinal) ||
                    (string.Equals(mode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(selectedPrepared, preparedPublish.Text.Trim(), StringComparison.Ordinal)))
                    return;

                sourceTestResult.Text = string.Join(Environment.NewLine, result.Lines);
                sourceTestResult.ForeColor = result.Failures == 0 ? Color.ForestGreen : Color.Firebrick;
                if (string.Equals(mode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
                    RefreshPreparedPublishValidation();
                else
                    AppendLog($"Source preflight completed: {Math.Max(0, result.Lines.Length - result.Failures)} passed, {result.Failures} failed.");
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                InternalDiagnostics.Record(ex, "Source preflight background task");
                if (!request.IsCancellationRequested && generation == sourcePreflightGeneration &&
                    !IsDisposed && !Disposing && !sourceTestResult.IsDisposed && _pageIndex == 0)
                {
                    sourceTestResult.Text = "FAIL — " + SecretRedactor.Redact(ex.Message);
                    sourceTestResult.ForeColor = Color.Firebrick;
                }
            }
            finally
            {
                if (ReferenceEquals(sourcePreflightCts, request)) sourcePreflightCts = null;
                request.Dispose();
            }
        }

        static (string[] Lines, int Failures) InspectSourcePreflight(
            EnvironmentService environment, string mode, string selectedSource, string selectedPrepared, CancellationToken ct)
        {
            var lines = new List<string>();
            var failures = 0;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(mode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
                {
                    var validation = DeploymentService.ValidatePreparedPublish(selectedPrepared);
                    ct.ThrowIfCancellationRequested();
                    lines.Add(validation.Ok ? "PASS — " + validation.Detail : "FAIL — " + validation.Detail);
                    if (!validation.Ok) failures++;
                    return (lines.ToArray(), failures);
                }
                if (!Directory.Exists(selectedSource))
                    throw new DirectoryNotFoundException("Select an existing source folder first.");
                var api = environment.DetectApiProject(selectedSource);
                ct.ThrowIfCancellationRequested();
                var structureOk = !string.IsNullOrWhiteSpace(api);
                lines.Add($"{(structureOk ? "PASS" : "FAIL")} — Web project structure: {(structureOk ? api : "ASP.NET Core Web .csproj not found")}");
                if (!structureOk) failures++;
                var frameworkOk = structureOk && File.Exists(api);
                var major = frameworkOk ? environment.DetectTargetFrameworkMajor(api!) : 0;
                lines.Add($"{(frameworkOk ? "PASS" : "FAIL")} — Target framework: {(frameworkOk ? ".NET " + major : "cannot detect without Web project")}");
                if (!frameworkOk) failures++;
                ct.ThrowIfCancellationRequested();
                var required = environment.DetectNodeRequirement(selectedSource, api ?? string.Empty);
                var packages = Directory.EnumerateFiles(selectedSource, "package.json", SearchOption.AllDirectories)
                    .Where(p => { ct.ThrowIfCancellationRequested(); return !IsGeneratedFrontendCandidate(selectedSource, p); })
                    .Take(10).ToArray();
                var viteRoots = packages.Select(Path.GetDirectoryName).Where(p => !string.IsNullOrWhiteSpace(p))
                    .Where(p => File.Exists(Path.Combine(p!, "index.html")) &&
                        Directory.EnumerateFiles(p!, "vite.config.*", SearchOption.TopDirectoryOnly).Any())
                    .ToArray();
                ct.ThrowIfCancellationRequested();
                lines.Add($"PASS — Frontend / Node assets: {(required ? "detected" : "not required")} ({packages.Length} source package.json candidate(s))");
                if (required && viteRoots.Length > 0) lines.Add($"PASS — Frontend root: {viteRoots[0]}");
                else if (required) { lines.Add("FAIL — Frontend detected but no complete source frontend root (index.html + vite.config.*) was found."); failures++; }
                _ = Directory.EnumerateFileSystemEntries(selectedSource, "*", SearchOption.TopDirectoryOnly).Take(1).ToArray();
                ct.ThrowIfCancellationRequested();
                lines.Add("PASS — Source access/readability");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures++;
                InternalDiagnostics.Record(ex, "Source preflight tests");
                lines.Add("FAIL — " + SecretRedactor.Redact(ex.Message));
            }
            return (lines.ToArray(), failures);
        }

        void ApplyDependencyMode()
        {
            _profile.DependencyResolutionMode = NormalizeDependencyMode(dependencyMode.SelectedItem?.ToString());
            _profile.OfflineBuild = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase);
            _appData.SaveDependencyResolutionMode(_profile.DependencyResolutionMode);
            _offlineDependenciesReady = false;
            offlineState.ForeColor = Color.DimGray;
            offlineState.Text = _profile.DependencyResolutionMode switch
            {
                "Offline Only" => "Local Repository -> System Cache -> FAIL. Use Sync Missing Online Once while Internet is available, then Test Offline Build.",
                "Hybrid" => "Local Repository -> System Cache -> Internet fallback. Sync is optional but recommended for future offline use.",
                _ => "Online mode: package restore may use Internet sources; offline readiness is not required."
            };
            var sourceBuild = !string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase);
            offlineAction.Enabled = sourceBuild && _profile.DependencyResolutionMode != "Online" && !_dependencySyncRunning;
            offlineTest.Enabled = sourceBuild && _profile.DependencyResolutionMode == "Offline Only" && !_dependencySyncRunning;
            UpdateNavigationState();
        }

        lockSource.CheckedChanged += (_, _) =>
        {
            // The path is always browse-only; locking also prevents folder changes.
            browse.Enabled = !lockSource.Checked;
            lockSource.Text = lockSource.Checked ? "Source locked" : "Lock source";
        };
        saveProfile.Click += (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            Directory.CreateDirectory(ProfilesFolder);
            using var dialog = new SaveFileDialog { Title = "Save deployment profile", Filter = "Deployment profile (*.json)|*.json", InitialDirectory = ProfilesFolder, FileName = string.IsNullOrWhiteSpace(_profile.ApplicationName) ? "deployment-profile.json" : _profile.ApplicationName.Replace(" ", "-") + ".json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var dto = DeploymentProfileSnapshot.From(_profile, lockSource.Checked);
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            AppendLog("[PROFILE] Saved: " + dialog.FileName + " (secrets excluded)");
            NotifyUi(UiNoticeKind.Success, "Profile saved (credentials excluded).");
        };
        loadProfile.Click += (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            Directory.CreateDirectory(ProfilesFolder);
            using var dialog = new OpenFileDialog { Title = "Load deployment profile", Filter = "Deployment profile (*.json)|*.json", InitialDirectory = ProfilesFolder };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var dto = JsonSerializer.Deserialize<DeploymentProfileSnapshot>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("Profile is empty.");
                dto.ApplyTo(_profile);
                source.Text = _profile.SourceFolder; app.Text = _profile.ApplicationName; site.Text = _profile.SiteName; database.Text = _profile.DatabaseName;
                repository.Text = _profile.DeploymentRepositoryFolder; workspace.Text = _profile.WorkspaceRoot; preparedPublish.Text = _profile.ReadyPublishFolder;
                buildMode.SelectedItem = buildMode.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), _profile.BuildMode, StringComparison.OrdinalIgnoreCase)) ?? buildMode.Items[0];
                dependencyMode.SelectedItem = NormalizeDependencyMode(_profile.DependencyResolutionMode);
                lockSource.Checked = dto.SourceLocked;
                AppendLog("[PROFILE] Loaded: " + dialog.FileName + " (secrets remain session-only)");
                NotifyUi(UiNoticeKind.Info, "Profile loaded.");
            }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Load deployment profile"); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Profile", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        exportSource.Click += (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            var src = source.Text.Trim(); if (!Directory.Exists(src)) { MessageBox.Show(this, "Select a valid source folder first.", "Clean Source Export", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using var options = new CleanExportOptionsForm(); if (options.ShowDialog(this) != DialogResult.OK) return;
            using var dialog = new SaveFileDialog { Title = "Export clean source ZIP", Filter = "ZIP archive (*.zip)|*.zip", FileName = $"{SanitizeFileName(_profile.ApplicationName)}-Last-Sources.zip" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { ExportCleanSourceWithProgress(src, dialog.FileName, options.Exclusions); MessageBox.Show(this, "Clean source ZIP created successfully.\r\n" + dialog.FileName, "Clean Source Export", MessageBoxButtons.OK, MessageBoxIcon.Information); AppendLog("[EXPORT] Clean source ZIP: " + dialog.FileName); }
            catch (Exception ex) { InternalDiagnostics.Record(ex, "Clean source export"); MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "Clean Source Export", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the new project release/source folder", SelectedPath = BrowseStartFolder(source.Text, BrowseContext.Source) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { source.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Source); }
        };
        browsePreparedPublish.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the final prepared ASP.NET Core IIS publish folder", SelectedPath = BrowseStartFolder(preparedPublish.Text, BrowseContext.Publish) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { preparedPublish.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Publish); }
        };
        void CommitAndValidatePreparedPublish()
        {
            var generation = ++_preparedValidationGeneration;
            var selected = preparedPublish.Text.Trim();
            try { selected = string.IsNullOrWhiteSpace(selected) ? string.Empty : Path.GetFullPath(selected); } catch { }
            _profile.ReadyPublishFolder = selected;
            if (preparedPublish.Text != selected) { preparedPublish.Text = selected; return; }
            RefreshPreparedPublishValidation();
            if (generation != _preparedValidationGeneration) return; // stale UI event must not overwrite newer selection
            if (string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase)) RunSourceTests();
            UpdateNavigationState();
        }
        preparedPublish.TextChanged += (_, _) => CommitAndValidatePreparedPublish();
        browseRepository.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = @"Select persistent Shared Dependency Repository (for example G:\Developer-Packages)", SelectedPath = BrowseStartFolder(repository.Text, BrowseContext.Repository) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { repository.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Repository); }
        };
        browseWorkspace.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the persistent Stable Build Workspace root", SelectedPath = BrowseStartFolder(workspace.Text, BrowseContext.Workspace) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { workspace.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Workspace); }
        };
        source.TextChanged += async (_, _) =>
        {
            Detect();
            _offlineDependenciesReady = false;
            _offlineDependenciesDetail = "Source changed; dependency readiness must be rechecked if required.";
            CancelBackgroundVerification();
            _scanCompleted = false;
            _iisAutoVerified = _runtimesAutoVerified = _deploymentLocationAutoVerified = false;
            UpdateStepIndicator();
            if (Directory.Exists(source.Text.Trim()))
            {
                RunSourceTests();
                var request = new CancellationTokenSource();
                _backgroundVerifyCts = request;
                try
                {
                    await Task.Delay(250, request.Token);
                    await RunBackgroundAutoVerificationAsync(request.Token);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                finally
                {
                    if (ReferenceEquals(_backgroundVerifyCts, request)) _backgroundVerifyCts = null;
                    request.Dispose();
                }
            }
            else { CancelSourcePreflight(); sourceTestResult.Text = "Waiting for a valid source folder..."; sourceTestResult.ForeColor = Color.DimGray; }
        };
        repository.TextChanged += (_, _) =>
        {
            _profile.DeploymentRepositoryFolder = repository.Text.Trim();
            _profile.InstallerCacheFolder = Path.Combine(_profile.DeploymentRepositoryFolder, "Installers");
            if (!string.IsNullOrWhiteSpace(_profile.DeploymentRepositoryFolder))
                QueueDeferredSettingsSave("repository", () => _appData.SaveDeploymentRepositoryFolder(_profile.DeploymentRepositoryFolder));
            _offlineDependenciesReady = false;
            UpdateNavigationState();
        };
        workspace.TextChanged += (_, _) =>
        {
            _profile.WorkspaceRoot = workspace.Text.Trim();
            if (!string.IsNullOrWhiteSpace(_profile.WorkspaceRoot))
                QueueDeferredSettingsSave("workspace", () => _appData.SaveWorkspaceRoot(_profile.WorkspaceRoot));
            UpdateNavigationState();
        };
        app.TextChanged += (_, _) => { _profile.ApplicationName = app.Text.Trim(); UpdateNavigationState(); };
        repository.Leave += (_, _) => FlushDeferredSettingsSaves();
        workspace.Leave += (_, _) => FlushDeferredSettingsSaves();
        site.TextChanged += (_, _) => { _profile.SiteName = site.Text.Trim(); _profile.AppPoolName = _profile.SiteName; UpdateNavigationState(); };
        database.TextChanged += (_, _) => { _profile.DatabaseName = database.Text.Trim(); UpdateNavigationState(); };
        void RefreshModeMeaning() => modeMeaning.Text = _profile.BuildMode switch
        {
            "Fast Local Test" => "Compile/test only. The IIS website is NOT updated, so the browser can still show the previously hosted version.",
            "Fast IIS Update" => "IIS deployment with fresh frontend build (when needed) and dotnet publish. Hosted-release reuse and fingerprint-based build skips are disabled pending release/rollback safety verification; dependency caches may still be reused.",
            "Full IIS Deploy" => "Complete publish, IIS/database preparation, health check and rollback protection.",
            "Prepared IIS Publish" => "Direct deploy of an already-built IIS publish. npm, frontend build, dotnet restore/build/publish and dependency sync are bypassed.",
            _ => "Uses the selected prepared-source/publish workflow."
        };
        buildMode.SelectedIndexChanged += (_, _) => { _profile.BuildMode = buildMode.SelectedItem?.ToString() ?? "Fast Local Test"; RefreshModeMeaning(); ApplyDependencyMode(); if (_profile.BuildMode == "Prepared IIS Publish") CommitAndValidatePreparedPublish(); else { RefreshPreparedPublishValidation(); UpdateNavigationState(); RunSourceTests(); } };
        dependencyMode.SelectedIndexChanged += (_, _) => ApplyDependencyMode();
        RefreshModeMeaning();
        RefreshPreparedPublishValidation();

        offlineAction.Click += async (_, _) =>
        {
            if (_dependencySyncRunning) return;
            _dependencySyncRunning = true;
            try
            {
                _profile.SourceFolder = source.Text.Trim();
                if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Select a valid source folder first.");
                if (string.IsNullOrWhiteSpace(_profile.DeploymentRepositoryFolder)) throw new InvalidOperationException("Select a persistent Shared Dependency Repository first.");
                _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder) ?? throw new InvalidOperationException("No ASP.NET Core Web .csproj was found under the selected source folder.");
                _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(_profile.ApiProjectPath);
                _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, _profile.ApiProjectPath);
                offlineAction.Enabled = false; offlineTest.Enabled = false;
                dependencySyncProgress.Visible = true; dependencySyncActivity.Visible = true;
                dependencySyncActivity.Text = "Checking NuGet and npm dependencies; downloading missing items...";
                offlineState.Text = "SYNC IN PROGRESS — reusable packages are being prepared in the Shared Dependency Repository.";
                offlineState.ForeColor = Color.RoyalBlue; _activity.Text = "Dependency sync running...";
                UpdateNavigationState();
                AppendLog("[ONLINE-ONCE] Operator requested one-time synchronization of missing dependencies.");
                var result = await PrepareOfflineRepositoryWithProgressAsync(CancellationToken.None);
                _offlineDependenciesReady = result.Ok; _offlineDependenciesDetail = result.Detail;
                offlineState.Text = result.Ok ? "OFFLINE REPOSITORY READY ✓ — " + result.Detail : "OFFLINE REPOSITORY INCOMPLETE ✕ — " + result.Detail;
                offlineState.ForeColor = result.Ok ? Color.ForestGreen : Color.Firebrick;
                dependencySyncActivity.Text = result.Ok ? "Dependency sync completed successfully." : "Dependency sync finished with missing/incomplete dependencies.";
                AppendLog(result.Ok ? "[ONLINE-ONCE] COMPLETE — OFFLINE REPOSITORY READY." : "[ONLINE-ONCE] COMPLETE — repository is still incomplete: " + result.Detail);
            }
            catch (Exception ex)
            {
                _offlineDependenciesReady = false; _offlineDependenciesDetail = GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message);
                offlineState.Text = "OFFLINE REPOSITORY INCOMPLETE ✕ — " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message); offlineState.ForeColor = Color.Firebrick;
                dependencySyncActivity.Text = "Dependency sync failed."; AppendLog("[ONLINE-ONCE] FAILED: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message));
                InternalDiagnostics.Record(ex, "Source page dependency sync");
            }
            finally
            {
                _dependencySyncRunning = false; dependencySyncProgress.Visible = false;
                ApplyDependencyMode();
                _activity.Text = _offlineDependenciesReady ? "Offline repository ready." : "Dependency sync finished; repository needs attention.";
                ApplyInteractiveCursors(this);
            }
        };
        offlineTest.Click += async (_, _) =>
        {
            try
            {
                _profile.SourceFolder = source.Text.Trim();
                if (!Directory.Exists(_profile.SourceFolder)) throw new InvalidOperationException("Select a valid source folder first.");
                _profile.ApiProjectPath = _environment.DetectApiProject(_profile.SourceFolder) ?? throw new InvalidOperationException("No ASP.NET Core Web .csproj was found under the selected source folder.");
                _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(_profile.ApiProjectPath);
                _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, _profile.ApiProjectPath);
                offlineTest.Enabled = false; offlineState.Text = "Testing with online package sources disabled...";
                AppendLog("[OFFLINE-TEST] Starting local-only dependency restore test.");
                var result = await _deployment.ValidateOfflinePackageAsync(_profile);
                _offlineDependenciesReady = result.Ok; _offlineDependenciesDetail = result.Detail;
                offlineState.Text = result.Ok ? "OFFLINE BUILD READY ✓" : "OFFLINE BUILD FAILED ✕ — " + result.Detail;
                offlineState.ForeColor = result.Ok ? Color.ForestGreen : Color.Firebrick;
            }
            catch (Exception ex)
            {
                _offlineDependenciesReady = false; _offlineDependenciesDetail = GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message);
                offlineState.Text = "OFFLINE BUILD FAILED ✕ — " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message); offlineState.ForeColor = Color.Firebrick;
                AppendLog("[OFFLINE-TEST] FAILED: " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message)); InternalDiagnostics.Record(ex, "Source page offline dependency test");
            }
            finally { ApplyDependencyMode(); }
        };

        RefreshModeMeaning();
        ApplyDependencyMode();
        // Returning to a selected valid source must preserve custom names.
        Detect(preserveIdentity: true);
        if (Directory.Exists(source.Text.Trim()) || string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
        {
            // Defer the first asynchronous preflight until the form has a UI
            // message pump; later page visits already have a window handle.
            if (IsHandleCreated) RunSourceTests();
            else Shown += (_, _) => { if (!sourceTestResult.IsDisposed) RunSourceTests(); };
        }
    }

    private void BuildTargetPage()
    {
        _pageTitle.Text = "Target and deployment identity";
        _pageDescription.Text = "Configure deployment identity, local/remote target, and prepared publish input when applicable.";
        var form = FormGrid();
        var sourceAlreadySelected = Directory.Exists(_profile.SourceFolder);
        var appName = new TextBox { Text = sourceAlreadySelected ? _profile.ApplicationName : string.Empty, Width = 210 };
        var siteName = new TextBox { Text = sourceAlreadySelected ? _profile.SiteName : string.Empty, Width = 210 };
        var databaseName = new TextBox { Text = sourceAlreadySelected ? _profile.DatabaseName : string.Empty, Width = 210 };
        var targetMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        targetMode.Items.AddRange(new object[] { "Local Server", "Remote IIS Server" });
        targetMode.SelectedItem = _profile.TargetMode;
        var host = new Ipv4AddressControl { Text = _profile.TargetMode == "Remote IIS Server" ? _profile.TargetHost : string.Empty };
        var remoteUser = new TextBox { Text = _profile.RemoteUserName, Width = 180 };
        var remotePassword = new TextBox { Text = _profile.RemotePassword, Width = 170, UseSystemPasswordChar = true };
        var remotePort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = _profile.RemoteWinRmPort, Width = 82 };
        var remoteSsl = new CheckBox { Text = "HTTPS / SSL", Checked = _profile.RemoteUseSsl, AutoSize = true };
        var savedRemote = _appData.GetRemoteCredential();
        var rememberRemote = new CheckBox { Text = "Save securely", Checked = savedRemote.Remember, AutoSize = true };
        var testRemote = new Button { Text = "Test / Bootstrap Remote", Width = 170 };
        var remoteState = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(690,0), Text = "Remote readiness not tested." };
        var publish = new TextBox { Text = _profile.ReadyPublishFolder, Dock = DockStyle.Fill };
        var browsePublish = new Button { Text = "Browse publish folder...", AutoSize = true };
        var detected = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(650, 0) };
        var framework = new Label { AutoSize = true, ForeColor = Color.DimGray };
        var node = new Label { AutoSize = true, ForeColor = Color.DimGray };
        var modeHelp = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0) };

        var identityRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        identityRow.Controls.AddRange(new Control[]
        {
            new Label { Text = "App", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, appName,
            new Label { Text = "Site", AutoSize = true, Padding = new Padding(10, 7, 4, 0) }, siteName,
            new Label { Text = "Database", AutoSize = true, Padding = new Padding(10, 7, 4, 0) }, databaseName
        });
        AddRow(form, "Identity", identityRow);
        AddRow(form, "Database behavior", new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(760, 0), Text = "Changing the database name connects the application to a different database; Deployment Manager does not delete or migrate data from the previous database automatically." });

        var targetRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        targetRow.Controls.AddRange(new Control[]
        {
            targetMode,
            new Label { Text = "Host / IP", AutoSize = true, Padding = new Padding(10, 7, 3, 0) }, host,
            new Label { Text = "User", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, remoteUser,
            new Label { Text = "Password", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, remotePassword,
            new Label { Text = "WinRM", AutoSize = true, Padding = new Padding(8, 7, 3, 0) }, remotePort,
            remoteSsl, rememberRemote, testRemote
        });
        AddRow(form, "Deployment target", targetRow);
        AddRow(form, "Remote readiness", remoteState);

        var publishRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        publishRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); publishRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        publishRow.Controls.Add(publish, 0, 0); publishRow.Controls.Add(browsePublish, 1, 0);
        AddRow(form, "Detected Web project", detected);
        AddRow(form, "Target framework", framework);
        AddRow(form, "Frontend", node);
        AddRow(form, "Mode behavior", modeHelp);
        _content.Controls.Add(form);

        void RefreshDetected()
        {
            var ready = _profile.BuildMode == "Prepared IIS Publish";
            publish.Enabled = browsePublish.Enabled = ready;
            if (ready)
            {
                detected.Text = "Prepared Publish: VALIDATION REQUIRED — source build is bypassed.";
                framework.Text = "Runtime/Hosting Bundle is still validated on the target machine.";
                node.Text = "Node.js/npm not required by Deployment Manager.";
                modeHelp.Text = "Use this when Visual Studio or another build machine has already produced a publish folder. The wizard only performs server/database/IIS deployment steps.";
                _profile.RequiresNode = false;
            }
            else
            {
                var api = _environment.DetectApiProject(_profile.SourceFolder);
                detected.Text = api ?? "Not detected";
                if (api is not null)
                {
                    _profile.ApiProjectPath = api;
                    _profile.TargetFrameworkMajor = _environment.DetectTargetFrameworkMajor(api);
                    _profile.RequiresNode = _environment.DetectNodeRequirement(_profile.SourceFolder, api);
                    framework.Text = $".NET {_profile.TargetFrameworkMajor}";
                    node.Text = _profile.RequiresNode ? "Node.js/npm detected" : "No Node.js frontend detected";
                }
                else { framework.Text = "—"; node.Text = "—"; }
                modeHelp.Text = _profile.BuildMode == "Visual Studio prepared"
                    ? "Open/build the solution once in Visual Studio first. Deployment Manager skips dependency restore where possible, but still performs the final frontend build (if needed) and dotnet publish."
                    : _profile.BuildMode == "Fast Local Test"
                        ? "Fast Local Test performs only change-aware frontend and incremental backend builds. It does not run dotnet publish, stage files, modify IIS, or prepare rollback."
                        : _profile.BuildMode == "Fast IIS Update"
                            ? "Fast IIS Update currently runs a fresh frontend build (if required) and dotnet publish before IIS activation. Hosted-binary/fingerprint skip shortcuts are disabled until release and rollback integrity are proven."
                            : "Full IIS Deploy creates a new publish and performs the complete IIS and database workflow.";
            }
            UpdateNavigationState();
        }

        bool IsValidIpv4(string value) => IPAddress.TryParse(value?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && string.Equals(ip.ToString(), value?.Trim(), StringComparison.Ordinal);
        bool ValidateRemoteInputs(bool showMessage)
        {
            if (!string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase)) return true;
            var ipText = host.Text.Trim(); string? error = null; Control? focus = null;
            if (string.IsNullOrWhiteSpace(ipText) || !IsValidIpv4(ipText)) { error = "Enter a valid IPv4 address for the remote IIS server (example: 192.168.1.50)."; focus = host; }
            else if (string.IsNullOrWhiteSpace(remoteUser.Text)) { error = "Remote User is required before testing or continuing."; focus = remoteUser; }
            else if (string.IsNullOrWhiteSpace(remotePassword.Text)) { error = "Remote Password is required before testing or continuing."; focus = remotePassword; }
            if (error is null) return true;
            remoteState.Text = "Remote target validation failed — " + error; remoteState.ForeColor = Color.Firebrick;
            if (showMessage) MessageBox.Show(this, error, "Remote target validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            focus?.Focus(); return false;
        }
        void ApplyTargetModeState()
        {
            _profile.TargetMode = targetMode.SelectedItem?.ToString() ?? "Local Server";
            var remoteSelected = _profile.TargetMode == "Remote IIS Server";
            host.Enabled = remoteUser.Enabled = remotePassword.Enabled = remotePort.Enabled = remoteSsl.Enabled = rememberRemote.Enabled = testRemote.Enabled = remoteSelected;
            remoteState.Text = remoteSelected ? "Remote readiness not tested." : "Local deployment selected.";
            remoteState.ForeColor = Color.DimGray;
            if (!remoteSelected) { _profile.TargetHost = Environment.MachineName; host.Text = string.Empty; }
        }
        targetMode.SelectedIndexChanged += (_, _) =>
        {
            // The remote host control is cleared in Local mode. Commit any
            // pending remote credentials while their original host is visible.
            FlushDeferredSettingsSaves();
            ApplyTargetModeState();
            UpdateNavigationState();
        };
        ApplyTargetModeState();
        void QueueRememberedRemoteCredential()
        {
            // Host is not a credential when the Local Server mode is selected.
            // Do not overwrite a stored remote host with the cleared local UI.
            if (rememberRemote.Checked &&
                string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
                QueueDeferredSettingsSave("remote-credential",
                    () => _appData.SaveRemoteCredential(host.Text.Trim(), remoteUser.Text.Trim(), remotePassword.Text, true));
        }
        remoteUser.TextChanged += (_, _) => { _profile.RemoteUserName = remoteUser.Text.Trim(); QueueRememberedRemoteCredential(); UpdateNavigationState(); };
        remotePassword.TextChanged += (_, _) => { _profile.RemotePassword = remotePassword.Text; QueueRememberedRemoteCredential(); UpdateNavigationState(); };
        remotePort.ValueChanged += (_, _) => _profile.RemoteWinRmPort = (int)remotePort.Value;
        remoteSsl.CheckedChanged += (_, _) => _profile.RemoteUseSsl = remoteSsl.Checked;
        rememberRemote.CheckedChanged += (_, _) =>
        {
            // Opting out must revoke the persisted secret immediately; do not
            // allow a queued Save(true) to re-create it after the checkbox is off.
            CancelDeferredSettingsSave("remote-credential");
            _appData.SaveRemoteCredential(host.Text.Trim(), remoteUser.Text.Trim(), remotePassword.Text, rememberRemote.Checked);
        };
        remoteUser.Leave += (_, _) => FlushDeferredSettingsSaves();
        remotePassword.Leave += (_, _) => FlushDeferredSettingsSaves();
        host.Leave += (_, _) => FlushDeferredSettingsSaves();
        testRemote.Click += async (_, _) =>
        {
            if (!ValidateRemoteInputs(showMessage: true)) return;
            try
            {
                testRemote.Enabled = false; remoteState.Text = "Checking remote server...";
                var rr = await _remote.CheckReadinessAsync(_profile);
                if (!rr.Connected && IsCredentialFailure(rr.Detail)) throw new InvalidOperationException("Remote username or password is incorrect.");
                remoteState.Text = rr.Detail; remoteState.ForeColor = rr.Connected && rr.IsAdministrator ? Color.ForestGreen : Color.Firebrick;
                if (rr.Connected && rr.IsAdministrator && (!rr.IisInstalled || !rr.HostingBundleInstalled))
                {
                    remoteState.Text += " Preparing missing prerequisites..."; await _remote.BootstrapAsync(_profile);
                    var rr2 = await _remote.CheckReadinessAsync(_profile); remoteState.Text = rr2.Detail;
                    remoteState.ForeColor = rr2.Connected && rr2.IsAdministrator && rr2.IisInstalled && rr2.HostingBundleInstalled ? Color.ForestGreen : Color.DarkOrange;
                }
            }
            catch (Exception ex) { remoteState.Text = "Remote NOT READY — " + GenericWeb.DeploymentManager.Services.SecretRedactor.Redact(ex.Message); remoteState.ForeColor = Color.Firebrick; InternalDiagnostics.Record(ex, "Remote readiness test"); }
            finally { testRemote.Enabled = string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase); }
        };
        browsePublish.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the already-published application folder", SelectedPath = BrowseStartFolder(publish.Text, BrowseContext.Publish) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { publish.Text = dialog.SelectedPath; RememberBrowseSelection(dialog.SelectedPath, BrowseContext.Publish); }
        };
        appName.TextChanged += (_, _) =>
        {
            var previousApplicationName = _profile.ApplicationName; _profile.ApplicationName = appName.Text.Trim();
            _profile.HostingRoot = UpdateAutoHostingRootForApplicationName(_profile.HostingRoot, previousApplicationName, _profile.ApplicationName);
            QueueDeferredSettingsSave("identity", () => _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName));
            QueueDeferredSettingsSave("hosting", () => _appData.SaveHostingSettings(_profile.HostingRoot, _profile.DeploymentLayout, _profile.CleanupPreviousDeployments, _profile.UseDefaultIisWebsite));
        };
        siteName.TextChanged += (_, _) => { _profile.SiteName = siteName.Text.Trim(); _profile.AppPoolName = _profile.SiteName; QueueDeferredSettingsSave("identity", () => _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName)); UpdateNavigationState(); };
        databaseName.TextChanged += (_, _) => { _profile.DatabaseName = databaseName.Text.Trim(); QueueDeferredSettingsSave("identity", () => _appData.SaveDeploymentIdentitySettings(_profile.ApplicationName, _profile.SiteName, _profile.DatabaseName)); UpdateNavigationState(); };
        appName.Leave += (_, _) => FlushDeferredSettingsSaves();
        siteName.Leave += (_, _) => FlushDeferredSettingsSaves();
        databaseName.Leave += (_, _) => FlushDeferredSettingsSaves();
        host.TextChanged += (_, _) =>
        {
            _profile.TargetHost = host.Text.Trim();
            if (string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
            {
                var valid = IsValidIpv4(host.Text); remoteState.Text = valid ? "IPv4 format valid. Remote readiness not tested." : "Enter IPv4 only, for example 192.168.1.50.";
                remoteState.ForeColor = valid ? Color.DimGray : Color.Firebrick;
            }
            QueueRememberedRemoteCredential();
            UpdateNavigationState();
        };
        publish.TextChanged += (_, _) => { _profile.ReadyPublishFolder = publish.Text.Trim(); UpdateNavigationState(); };
        RefreshDetected();
    }

}
