using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using GenericWeb.DeploymentManager.UI.Pages;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private void BuildDeploymentLocationPage()
    {
        _pageTitle.Text = "Deployment location";
        _pageDescription.Text = "Choose exactly where IIS should host the application. You can use a dedicated application folder or explicitly take over the IIS default web root on port 80.";
        var form = FormGrid();
        var lastCustomRoot = string.Equals(_profile.HostingRoot.TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(_profile.ApplicationName))
            : _profile.HostingRoot;
        var useDefault = new CheckBox
        {
            Text = @"Use IIS default web root (C:\inetpub\wwwroot) and port 80 — replace the existing IIS site/content on port 80",
            AutoSize = true,
            Checked = _profile.UseDefaultIisWebsite,
            ForeColor = Color.DarkRed
        };
        var hostingRoot = new TextBox { Text = _profile.HostingRoot, Dock = DockStyle.Fill, MinimumSize = new Size(650, 38), Font = new Font("Segoe UI", 10F) };
        var browse = new Button { Text = "Browse...", Width = 120, Height = 38 };
        var single = new RadioButton { Text = "Single Folder — always deploy the active site into this exact folder", AutoSize = true, Checked = string.Equals(_profile.DeploymentLayout, "Single Folder", StringComparison.OrdinalIgnoreCase) };
        var versioned = new RadioButton { Text = "Versioned Folders — keep releases as subfolders under this root", AutoSize = true, Checked = !string.Equals(_profile.DeploymentLayout, "Single Folder", StringComparison.OrdinalIgnoreCase) };
        var cleanup = new CheckBox { Text = "After a successful versioned deployment, delete older release folders", AutoSize = true, Checked = _profile.CleanupPreviousDeployments, Enabled = versioned.Checked };
        var hint = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(720, 0), Text = @"Dedicated folder example: C:\inetpub\wwwroot\NSPOfficeAutomation
Default website mode: C:\inetpub\wwwroot, HTTP port 80. A different IIS site owning port 80 blocks deployment; reconfigure its binding explicitly first." };

        AddRow(form, "Application", new TextBox { Text = _profile.ApplicationName, Dock = DockStyle.Fill, ReadOnly = true });
        AddRow(form, "Default IIS website", useDefault);
        AddRow(form, "Hosted site folder", Wrap(hostingRoot, browse));
        AddRow(form, "Deployment layout", VerticalWrap(single, versioned, cleanup));
        AddRow(form, "", hint);
        _content.Controls.Add(form);

        void ApplyDefaultMode()
        {
            if (useDefault.Checked)
            {
                if (!string.Equals(hostingRoot.Text.TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase)) lastCustomRoot = hostingRoot.Text.Trim();
                hostingRoot.Text = @"C:\inetpub\wwwroot";
                single.Checked = true;
                _profile.HttpPort = 80;
                _profile.HostHeader = string.Empty;
            }
            else if (string.Equals(hostingRoot.Text.TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase))
            {
                hostingRoot.Text = string.IsNullOrWhiteSpace(lastCustomRoot) ? Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(_profile.ApplicationName)) : lastCustomRoot;
            }
            hostingRoot.ReadOnly = useDefault.Checked;
            browse.Enabled = !useDefault.Checked;
            single.Enabled = !useDefault.Checked;
            versioned.Enabled = !useDefault.Checked;
            cleanup.Enabled = !useDefault.Checked && versioned.Checked;
        }

        void Save()
        {
            _profile.UseDefaultIisWebsite = useDefault.Checked;
            _profile.HostingRoot = hostingRoot.Text.Trim();
            _profile.DeploymentLayout = useDefault.Checked || single.Checked ? "Single Folder" : "Versioned Folders";
            _profile.CleanupPreviousDeployments = cleanup.Checked;
            if (_profile.UseDefaultIisWebsite) { _profile.HttpPort = 80; _profile.HostHeader = string.Empty; }
            cleanup.Enabled = !useDefault.Checked && versioned.Checked;
            QueueDeferredSettingsSave("hosting", () => _appData.SaveHostingSettings(_profile.HostingRoot, _profile.DeploymentLayout, _profile.CleanupPreviousDeployments, _profile.UseDefaultIisWebsite));
            UpdateNavigationState();
        }

        hostingRoot.TextChanged += (_, _) => Save();
        hostingRoot.Leave += (_, _) => FlushDeferredSettingsSaves();
        single.CheckedChanged += (_, _) => Save();
        versioned.CheckedChanged += (_, _) => Save();
        cleanup.CheckedChanged += (_, _) => Save();
        useDefault.CheckedChanged += (_, _) => { ApplyDefaultMode(); Save(); };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the folder where IIS site files should be hosted", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(hostingRoot.Text) ? hostingRoot.Text : string.Empty, ShowNewFolderButton = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) hostingRoot.Text = dialog.SelectedPath;
        };
        ApplyDefaultMode();
        Save();
    }

    private double _reviewSplitRatio = 0.42;

    private void BuildReviewPage()
    {
        _pageTitle.Text = "Review and deploy";
        _pageDescription.Text = "Review the plan. Depending on Build mode, Deploy either builds the source or uses prepared publish files, then prepares the database, configures IIS, and performs the health check.";
        var deploymentReady = (_persistentState.LastResult == "Successful" && !string.IsNullOrWhiteSpace(_lastSuccessfulPublishPath)) || Directory.Exists(_profile.HostingRoot);
        var host = string.IsNullOrWhiteSpace(_profile.HostHeader) ? "localhost" : _profile.HostHeader;
        var siteUrl = _profile.HttpPort == 80 ? $"http://{host}" : $"http://{host}:{_profile.HttpPort}";
        var folder = string.IsNullOrWhiteSpace(_lastSuccessfulPublishPath) ? _profile.HostingRoot : _lastSuccessfulPublishPath;
        var state = new ReviewStageState(_profile.BuildMode, DescribeReview(), _deploymentStatus,
            _profile.CreateBackupBeforeDeployment, _profile.KeepPreviousDeploymentForRollback,
            _profile.PreviousDeploymentRetentionCount, _operationRunning, deploymentReady,
            _activity.Text, _deploymentElapsedText, ReviewElapsedColor(), _logViewMode.ToString(), _reviewSplitRatio);
        var view = new ReviewStageView(state, _log);
        _reviewView = view;
        _content.Controls.Add(view);
        view.Activate(new ReviewStageActions(
            index =>
            {
                _profile.BuildMode = index switch { 1 => "Fast IIS Update", 2 => "Full IIS Deploy", _ => "Fast Local Test" };
                InvalidateEnvironmentEvidence();
                UpdateDashboard();
                return DescribeReview();
            },
            (backup, keep, count) =>
            {
                if (backup.HasValue) _profile.CreateBackupBeforeDeployment = backup.Value;
                if (keep.HasValue) _profile.KeepPreviousDeploymentForRollback = keep.Value;
                if (count.HasValue) _profile.PreviousDeploymentRetentionCount = count.Value;
                _appData.SaveProjectProfile(_profile);
            },
            selected =>
            {
                if (Enum.TryParse<LogViewMode>(selected, out var mode)) { _logViewMode = mode; RenderVisibleLog(); }
            },
            ratio => _reviewSplitRatio = ratio,
            () => OpenSiteWithBrowser(siteUrl, new BrowserChoice("System Default", null)),
            () => OpenTarget(folder!)));
    }

    private Color ReviewElapsedColor() => _persistentState.LastResult switch
    {
        "Successful" => Color.ForestGreen,
        "Failed" or "RecoveryFailed" => Color.Firebrick,
        "Cancelled" => Color.DarkOrange,
        _ => Color.RoyalBlue
    };

    private string DescribeReview() => $"Application: {_profile.ApplicationName}\r\nBuild mode (selected on Project Source): {_profile.BuildMode}\r\nHosted website action: {(_profile.BuildMode == "Fast Local Test" ? "UNCHANGED — compile only" : "UPDATE after successful build")}\r\nDependency mode: {_profile.DependencyResolutionMode}\r\nOffline build: {_profile.OfflineBuild}\r\nTarget mode: {_profile.TargetMode}\r\nTarget: {_profile.TargetHost}\r\nDeployment Repository: {_profile.DeploymentRepositoryFolder}\r\nStable Build Workspace: {_profile.WorkspaceRoot}\r\nIncoming Source: {_profile.SourceFolder}\r\nReady publish folder: {_profile.ReadyPublishFolder}\r\nAPI project: {_profile.ApiProjectPath}\r\nTarget framework: .NET {_profile.TargetFrameworkMajor}\r\nNode required: {_profile.RequiresNode}\r\nSQL: {_profile.SqlInstance}\r\nDatabase: {_profile.DatabaseName}\r\nHosted site folder: {_profile.HostingRoot}\r\nDeployment layout: {_profile.DeploymentLayout}\r\nDefault IIS website: {_profile.UseDefaultIisWebsite}\r\nPersistent data root: {_profile.DeploymentRoot}\r\nIIS site: {_profile.SiteName}\r\nApp pool: {_profile.AppPoolName}\r\nPort: {_profile.HttpPort}\r\nHealth: {_profile.HealthPath}";

}
