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
        var fallbackRoot = Path.Combine(@"C:\inetpub\wwwroot", SafeApplicationFolderName(_profile.ApplicationName));
        var view = new DeploymentLocationView(_profile.ApplicationName, _profile.HostingRoot, _profile.DeploymentLayout,
            _profile.UseDefaultIisWebsite, _profile.CleanupPreviousDeployments, fallbackRoot);
        _locationView = view;
        _content.Controls.Add(view);
        view.Activate(choice =>
        {
            _profile.UseDefaultIisWebsite = choice.UseDefault;
            _profile.HostingRoot = choice.Folder;
            _profile.DeploymentLayout = choice.Layout;
            _profile.CleanupPreviousDeployments = choice.CleanupOlder;
            if (_profile.UseDefaultIisWebsite) { _profile.HttpPort = 80; _profile.HostHeader = string.Empty; }
            SaveHostingSettings(defer: true);
            UpdateNavigationState();
        }, FlushDeferredSettingsSaves);
        view.PublishCurrent();
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
