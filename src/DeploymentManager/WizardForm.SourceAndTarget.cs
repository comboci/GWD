using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using static GenericWeb.DeploymentManager.UI.Controls.WizardLayout;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private void BuildProjectSourcePage()
    {
        _pageTitle.Text = "Select source and dependency strategy";
        _pageDescription.Text = "Choose the incoming source and all build/dependency settings here. Source preflight runs automatically. Persistent dependencies and stable build workspaces stay outside the versioned source folder.";
        var view = new UI.Pages.DeploymentSourceView(_profile.SourceFolder, _sourceLocked, _profile.ApplicationName,
            _profile.SiteName, _profile.DatabaseName, _profile.BuildMode, NormalizeDependencyMode(_profile.DependencyResolutionMode),
            _profile.ReadyPublishFolder, _profile.DeploymentRepositoryFolder, _profile.WorkspaceRoot);
        var source = view.Source;
        var browse = view.Browse;
        var lockSource = view.LockSource;
        var saveProfile = view.SaveProfile;
        var loadProfile = view.LoadProfile;
        var exportSource = view.ExportSource;
        var detectedRoot = view.DetectedRoot;
        var app = view.App;
        var site = view.SiteInput;
        var database = view.Database;
        var buildMode = view.BuildMode;
        var dependencyMode = view.DependencyMode;
        var modeMeaning = view.ModeMeaning;
        var preparedPublish = view.PreparedPublish;
        var browsePreparedPublish = view.BrowsePreparedPublish;
        var preparedValidation = view.PreparedValidation;
        var repository = view.Repository;
        var browseRepository = view.BrowseRepository;
        var workspace = view.Workspace;
        var browseWorkspace = view.BrowseWorkspace;
        var offlineAction = view.OfflineAction;
        var offlineTest = view.OfflineTest;
        var offlineState = view.OfflineState;
        var dependencySyncProgress = view.DependencySyncProgress;
        var dependencySyncActivity = view.DependencySyncActivity;
        var sourceTestResult = view.SourceTestResult;
        var recheckSource = view.RecheckSource;
        _saveProfileAction = saveProfile;
        _loadProfileAction = loadProfile;
        _lockSourceAction = lockSource;
        _exportSourceAction = exportSource;
        _content.Controls.Add(view);

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
            if (string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
            {
                // A prebuilt artifact does not require a source project.
                // Do not erase or infer deployment identity from an absent source.
                detectedRoot.Text = "Source project not required in Prepared IIS Publish mode.";
                detectedRoot.ForeColor = Color.DimGray;
                RefreshPreparedPublishValidation();
                UpdateNavigationState();
                return;
            }
            var api = Directory.Exists(_profile.SourceFolder) ? _environment.DetectApiProject(_profile.SourceFolder) : null;
            RememberNavigationSourceReadiness(false, _profile.SourceFolder, !string.IsNullOrWhiteSpace(api));
            if (api is null)
            {
                _profile.ApiProjectPath = string.Empty;
                detectedRoot.Text = "No ASP.NET Core Web project detected."; detectedRoot.ForeColor = Color.Firebrick;
                if (!preserveIdentity) app.Text = site.Text = database.Text = string.Empty;
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

        void ApplyDependencyMode(bool invalidate = true)
        {
            _profile.DependencyResolutionMode = NormalizeDependencyMode(dependencyMode.SelectedItem?.ToString());
            _profile.OfflineBuild = string.Equals(_profile.DependencyResolutionMode, "Offline Only", StringComparison.OrdinalIgnoreCase);
            SaveDependencyMode(_profile.DependencyResolutionMode);
            if (invalidate)
            {
                _offlineDependenciesReady = false;
                _offlineDependenciesDetail = "Dependency readiness must be checked again.";
            }
            offlineState.ForeColor = Color.DimGray;
            offlineState.Text = _profile.DependencyResolutionMode switch
            {
                "Offline Only" => "Local Repository -> System Cache -> FAIL. Use Sync Missing Online Once while Internet is available, then Test Offline Build.",
                "Hybrid" => "Local Repository -> System Cache -> Internet fallback. Sync is optional but recommended for future offline use.",
                _ => "Online mode: package restore may use Internet sources; offline readiness is not required."
            };
            var sourceBuild = !string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase);
            if (!invalidate && sourceBuild && _offlineDependenciesReady)
            {
                offlineState.ForeColor = Color.ForestGreen;
                offlineState.Text = "OFFLINE REPOSITORY READY — " + _offlineDependenciesDetail;
            }
            offlineAction.Enabled = sourceBuild && _profile.DependencyResolutionMode != "Online" && !_dependencySyncRunning;
            offlineTest.Enabled = sourceBuild && _profile.DependencyResolutionMode == "Offline Only" && !_dependencySyncRunning;
            UpdateNavigationState();
        }

        lockSource.CheckedChanged += (_, _) =>
        {
            // The path is always browse-only; locking also prevents folder changes.
            _sourceLocked = lockSource.Checked;
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
                // Validate required serialized fields before mutating the live profile.
                if (dto.ApplicationName is null || dto.SiteName is null || dto.DatabaseName is null ||
                    dto.SourceFolder is null || dto.ReadyPublishFolder is null || dto.BuildMode is null ||
                    dto.DependencyResolutionMode is null || dto.DeploymentRepositoryFolder is null || dto.WorkspaceRoot is null)
                    throw new InvalidDataException("Profile is missing required fields.");
                FlushDeferredSettingsSaves();
                var sqlIdentityChanged = !string.Equals(_profile.SqlInstance, dto.SqlInstance, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_profile.SqlUserName, dto.SqlUserName, StringComparison.OrdinalIgnoreCase) ||
                    _profile.UseWindowsAuthentication != dto.UseWindowsAuthentication;
                dto.ApplyTo(_profile);
                if (sqlIdentityChanged)
                {
                    _profile.SqlPassword = string.Empty;
                    _profile.SqlCredentialConfigured = false;
                }
                _sourceLocked = dto.SourceLocked;
                _profile.InstallerCacheFolder = Path.Combine(_profile.DeploymentRepositoryFolder, "Installers");
                if (!string.IsNullOrWhiteSpace(_profile.DeploymentRepositoryFolder))
                    SaveFolderSetting("repository", _profile.DeploymentRepositoryFolder, _appData.SaveDeploymentRepositoryFolder, defer: false);
                if (!string.IsNullOrWhiteSpace(_profile.WorkspaceRoot))
                    SaveFolderSetting("workspace", _profile.WorkspaceRoot, _appData.SaveWorkspaceRoot, defer: false);
                _offlineDependenciesReady = false;
                InvalidateEnvironmentEvidence();
                InvalidateNavigationSourceReadiness();
                // Reconstruct once from the complete snapshot. Intermediate
                // TextChanged events must not overwrite imported identity.
                RebuildCurrentPage();
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
            if (!string.Equals(_profile.ReadyPublishFolder, selected, StringComparison.Ordinal))
                InvalidateEnvironmentEvidence();
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
            InvalidateEnvironmentEvidence();
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
                SaveFolderSetting("repository", _profile.DeploymentRepositoryFolder, _appData.SaveDeploymentRepositoryFolder, defer: true);
            _offlineDependenciesReady = false;
            UpdateNavigationState();
        };
        workspace.TextChanged += (_, _) =>
        {
            _profile.WorkspaceRoot = workspace.Text.Trim();
            _offlineDependenciesReady = false;
            if (!string.IsNullOrWhiteSpace(_profile.WorkspaceRoot))
                SaveFolderSetting("workspace", _profile.WorkspaceRoot, _appData.SaveWorkspaceRoot, defer: true);
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
        buildMode.SelectedIndexChanged += (_, _) => { _profile.BuildMode = buildMode.SelectedItem?.ToString() ?? "Fast Local Test"; InvalidateEnvironmentEvidence(); RefreshModeMeaning(); ApplyDependencyMode(); if (_profile.BuildMode == "Prepared IIS Publish") CommitAndValidatePreparedPublish(); else { RefreshPreparedPublishValidation(); Detect(preserveIdentity: true); RunSourceTests(); } };
        recheckSource.Click += (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            InvalidateNavigationSourceReadiness();
            InvalidateEnvironmentEvidence();
            Detect(preserveIdentity: true);
            RunSourceTests();
        };
        dependencyMode.SelectedIndexChanged += (_, _) => ApplyDependencyMode();
        RefreshModeMeaning();
        RefreshPreparedPublishValidation();

        offlineAction.Click += async (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            CancelBackgroundVerification();
            _dependencySyncRunning = true;
            UpdateNavigationState();
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
                _dependencySyncRunning = false;
                if (!IsDisposed && !Disposing && !view.IsDisposed && !view.Disposing)
                {
                    dependencySyncProgress.Visible = false;
                    offlineAction.Enabled = _profile.DependencyResolutionMode != "Online";
                    offlineTest.Enabled = _profile.DependencyResolutionMode == "Offline Only";
                    UpdateNavigationState();
                    _activity.Text = _offlineDependenciesReady ? "Offline repository ready." : "Dependency sync finished; repository needs attention.";
                    ApplyInteractiveCursors(this);
                }
            }
        };
        offlineTest.Click += async (_, _) =>
        {
            if (!CanNavigateInteractively()) return;
            CancelBackgroundVerification();
            _dependencySyncRunning = true;
            UpdateNavigationState();
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
            finally
            {
                _dependencySyncRunning = false;
                if (!IsDisposed && !Disposing && !view.IsDisposed && !view.Disposing)
                {
                    offlineAction.Enabled = _profile.DependencyResolutionMode != "Online";
                    offlineTest.Enabled = _profile.DependencyResolutionMode == "Offline Only";
                    UpdateNavigationState();
                }
            }
        };

        RefreshModeMeaning();
        ApplyDependencyMode(invalidate: false);
        // Returning to a selected valid source must preserve custom names.
        Detect(preserveIdentity: true);
        if (Directory.Exists(source.Text.Trim()) || string.Equals(_profile.BuildMode, "Prepared IIS Publish", StringComparison.OrdinalIgnoreCase))
        {
            // Defer the first asynchronous preflight until the form has a UI
            // message pump; later page visits already have a window handle.
            if (IsHandleCreated) RunSourceTests();
            else
            {
                void RunInitialSourceTests(object? sender, EventArgs args)
                {
                    Shown -= RunInitialSourceTests;
                    if (!sourceTestResult.IsDisposed && _pageIndex == 0) RunSourceTests();
                }
                Shown += RunInitialSourceTests;
                sourceTestResult.Disposed += (_, _) => Shown -= RunInitialSourceTests;
            }
        }
    }

    private void BuildTargetPage()
    {
        _pageTitle.Text = "Target and deployment identity";
        _pageDescription.Text = "Configure deployment identity, local/remote target, and prepared publish input when applicable.";
        var savedCredential = _appData.GetRemoteCredential(out var savedCredentialGeneration);
        Guid? generation = savedCredential.Remember &&
            string.Equals(savedCredential.UserName, _profile.RemoteUserName, StringComparison.Ordinal) &&
            string.Equals(savedCredential.Password, _profile.RemotePassword, StringComparison.Ordinal) &&
            (!string.Equals(_profile.TargetMode, "Remote IIS Server", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(savedCredential.Host, _profile.TargetHost, StringComparison.Ordinal))
            ? savedCredentialGeneration : null;
        var view = new UI.Pages.DeploymentTargetView(Directory.Exists(_profile.SourceFolder),
            _profile.ApplicationName, _profile.SiteName, _profile.DatabaseName, _profile.TargetMode,
            _profile.TargetHost, _profile.RemoteUserName, _profile.RemotePassword, _profile.RemoteWinRmPort,
            _profile.RemoteUseSsl, savedCredential.Remember, _profile.ReadyPublishFolder);
        var appName = view.AppName;
        var siteName = view.SiteName;
        var databaseName = view.DatabaseName;
        var targetMode = view.TargetMode;
        var host = view.Host;
        var remoteUser = view.RemoteUser;
        var remotePassword = view.RemotePassword;
        var remotePort = view.RemotePort;
        var remoteSsl = view.RemoteSsl;
        var rememberRemote = view.RememberRemote;
        var testRemote = view.TestRemote;
        var remoteState = view.RemoteState;
        var publish = view.Publish;
        var browsePublish = view.BrowsePublish;
        var detected = view.Detected;
        var framework = view.Framework;
        var node = view.Node;
        var modeHelp = view.ModeHelp;
        _content.Controls.Add(view);

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
            InvalidateEnvironmentEvidence();
            ApplyTargetModeState();
            UpdateNavigationState();
        };
        ApplyTargetModeState();
        void QueueRememberedRemoteCredential()
        {
            // Host is not a credential when the Local Server mode is selected.
            // Do not overwrite a stored remote host with the cleared local UI.
            if (rememberRemote.Checked && generation is Guid currentGeneration &&
                string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
            {
                var values = (Host: host.Text.Trim(), User: remoteUser.Text.Trim(), Password: remotePassword.Text);
                QueueDeferredSettingsSave("remote-credential", () =>
                {
                    // A different instance or an explicit opt-out can retire this generation.
                    _appData.SaveRememberedRemoteCredential(currentGeneration, values.Host, values.User, values.Password);
                });
            }
        }
        remoteUser.TextChanged += (_, _) => { _profile.RemoteUserName = remoteUser.Text.Trim(); QueueRememberedRemoteCredential(); UpdateNavigationState(); };
        remotePassword.TextChanged += (_, _) => { _profile.RemotePassword = remotePassword.Text; QueueRememberedRemoteCredential(); UpdateNavigationState(); };
        remotePort.ValueChanged += (_, _) => _profile.RemoteWinRmPort = (int)remotePort.Value;
        remoteSsl.CheckedChanged += (_, _) => _profile.RemoteUseSsl = remoteSsl.Checked;
        var changingRememberState = false;
        rememberRemote.CheckedChanged += (_, _) =>
        {
            if (changingRememberState) return;
            CancelDeferredSettingsSave("remote-credential");
            var remember = rememberRemote.Checked;
            try
            {
                // Immediate barrier under the same persistent writer. Already
                // enqueued older edits cannot write back after this returns.
                var saved = _appData.SetRemoteCredentialPreference(host.Text.Trim(), remoteUser.Text.Trim(), remotePassword.Text, remember);
                generation = remember ? saved : null;
            }
            catch (Exception ex)
            {
                changingRememberState = true;
                try { rememberRemote.Checked = !remember; }
                finally { changingRememberState = false; }
                InternalDiagnostics.Record(new IOException("Remote preference write failed (" + ex.GetType().Name + ")."), "Save Settings");
                NotifyUi(UiNoticeKind.Warning, "The remote credential preference was not saved.");
            }
        };
        remoteUser.Leave += (_, _) => FlushDeferredSettingsSaves();
        remotePassword.Leave += (_, _) => FlushDeferredSettingsSaves();
        host.Leave += (_, _) => FlushDeferredSettingsSaves();
        testRemote.Click += async (_, _) =>
        {
            if (!CanNavigateInteractively() || !ValidateRemoteInputs(showMessage: true)) return;
            await BusyAsync(testRemote, async () =>
            {
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
            finally { if (!testRemote.IsDisposed) testRemote.Enabled = string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase); }
            });
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
            SaveIdentitySettings(defer: true);
            SaveHostingSettings(defer: true);
        };
        siteName.TextChanged += (_, _) => { _profile.SiteName = siteName.Text.Trim(); _profile.AppPoolName = _profile.SiteName; SaveIdentitySettings(defer: true); UpdateNavigationState(); };
        databaseName.TextChanged += (_, _) => { _profile.DatabaseName = databaseName.Text.Trim(); SaveIdentitySettings(defer: true); UpdateNavigationState(); };
        appName.Leave += (_, _) => FlushDeferredSettingsSaves();
        siteName.Leave += (_, _) => FlushDeferredSettingsSaves();
        databaseName.Leave += (_, _) => FlushDeferredSettingsSaves();
        host.TextChanged += (_, _) =>
        {
            if (!string.Equals(_profile.TargetHost, host.Text.Trim(), StringComparison.Ordinal)) InvalidateEnvironmentEvidence();
            _profile.TargetHost = host.Text.Trim();
            if (string.Equals(targetMode.SelectedItem?.ToString(), "Remote IIS Server", StringComparison.OrdinalIgnoreCase))
            {
                var valid = IsValidIpv4(host.Text); remoteState.Text = valid ? "IPv4 format valid. Remote readiness not tested." : "Enter IPv4 only, for example 192.168.1.50.";
                remoteState.ForeColor = valid ? Color.DimGray : Color.Firebrick;
            }
            QueueRememberedRemoteCredential();
            UpdateNavigationState();
        };
        publish.TextChanged += (_, _) => { InvalidateEnvironmentEvidence(); _profile.ReadyPublishFolder = publish.Text.Trim(); UpdateNavigationState(); };
        RefreshDetected();
    }

}
