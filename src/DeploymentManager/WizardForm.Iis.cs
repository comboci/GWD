using GenericWeb.DeploymentManager.Models;
using GenericWeb.DeploymentManager.Services;
using GenericWeb.DeploymentManager.UI.Pages;
using static GenericWeb.DeploymentManager.UI.Controls.StatusCardUi;

namespace GenericWeb.DeploymentManager;

public sealed partial class WizardForm
{
    private void BuildIisPage()
    {
        _pageTitle.Text = "IIS Web Server";
        _pageDescription.Text = "Detect/enable IIS and, when needed, manage test sites directly. Advanced IIS Management can start, stop, or completely remove selected IIS sites and their physical files.";
        var view = new IisManagementView(_iisState.Installed, _iisState.Detail, _profile.UseDefaultIisWebsite,
            _profile.HttpPort, _profile.HostHeader, _profile.HealthPath, _profile.HostingRoot, _profile.SiteName, _profile.AppPoolName);
        var install = view.Install;
        var refresh = view.RefreshButton;
        var advanced = view.Advanced;
        var recoveryReview = view.RecoveryReview;
        var management = view.Management;
        var refreshSites = view.RefreshSites;
        var startSites = view.StartSites;
        var stopSites = view.StopSites;
        var removeSites = view.RemoveSites;
        var lastIisCheck = view.LastIisCheck;
        var grid = view.Grid;
        var startPool = view.StartPool;
        var stopPool = view.StopPool;
        var recyclePool = view.RecyclePool;
        var deletePool = view.DeletePool;
        var poolGrid = view.PoolGrid;
        var targetPort = view.TargetPort;
        var targetHost = view.TargetHost;
        var targetHealth = view.TargetHealth;
        var status = view.Status;
        recoveryReview.Click += async (_, _) =>
        {
            if (_operationRunning || _autoDeployRunning || _dependencySyncRunning) return;
            using var dialog = new Form { Text = "Review previous deployment", Width = 680, Height = 260, StartPosition = FormStartPosition.CenterParent };
            GwdUi.ConfigureForm(dialog);
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(12) };
            var explanation = new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "Enter the prior resolved attempt ID from the diagnostic report. Independently verify the database and backup before confirming. IIS, HTTP, API and frontend identity will be checked again. Unresolved journals remain blocked." };
            var owner = new TextBox { Width = 580 };
            var verified = new CheckBox { AutoSize = true, Text = "I verified the database and backup/recovery evidence." };
            var confirm = new Button { Text = "Verify and archive markers", AutoSize = true, DialogResult = DialogResult.OK };
            layout.Controls.AddRange(new Control[] { explanation, owner, verified, confirm });
            dialog.Controls.Add(layout);
            layout.ClientSizeChanged += (_, _) =>
            {
                var width = Math.Max(140, layout.ClientSize.Width - layout.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
                explanation.MaximumSize = verified.MaximumSize = new Size(width, 0);
                owner.Width = Math.Min(UI.Controls.ResponsiveLayout.Pixels(dialog, 580), width);
            };
            if (dialog.ShowDialog(this) != DialogResult.OK || !verified.Checked || string.IsNullOrWhiteSpace(owner.Text)) return;
            _operationRunning = true;
            recoveryReview.Enabled = false;
            UpdateNavigationState();
            try
            {
                await _deployment.ReviewResolvedDeploymentAsync(_profile, owner.Text.Trim());
                AppendLog("[RECOVERY] Verified prior resolved deployment; markers archived. Retry Auto Deploy.");
                MessageBox.Show(this, "Recovery markers archived. Retry Auto Deploy.", "Recovery review", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { AppendLog("[RECOVERY] " + ex.Message); MessageBox.Show(this, ex.Message, "Recovery review blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { _operationRunning = false; recoveryReview.Enabled = true; UpdateNavigationState(); }
        };
        targetPort.ValueChanged += (_, _) => { _profile.HttpPort = _profile.UseDefaultIisWebsite ? 80 : (int)targetPort.Value; UpdateNavigationState(); };
        targetHost.TextChanged += (_, _) => _profile.HostHeader = _profile.UseDefaultIisWebsite ? string.Empty : targetHost.Text.Trim();
        targetHealth.TextChanged += (_, _) => { _profile.HealthPath = targetHealth.Text.Trim(); _healthMonitor.SubmitProfile(_profile); };
        _content.Controls.Add(view);

        async Task RefreshIisStatus()
        {
            var result = await _environment.CheckIisAsync();
            if (view.IsDisposed || view.Disposing || status.IsDisposed || _pageIndex != 3) return;
            _iisState = result;
            UpdateStatusCard(status, "IIS status", _iisState.Detail, _iisState.Installed);
            install.Enabled = !_iisState.Installed; advanced.Enabled = _iisState.Installed;
            if (!_iisState.Installed) { advanced.Checked = false; management.Visible = false; }
            UpdateNavigationState();
        }

        CancellationTokenSource? inventoryRequest = null;
        grid.Disposed += (_, _) => inventoryRequest?.Cancel();

        async Task LoadSites()
        {
            if (!_iisState.Installed || grid.IsDisposed || poolGrid.IsDisposed) return;
            inventoryRequest?.Cancel();
            var request = new CancellationTokenSource();
            inventoryRequest = request;
            refreshSites.Enabled = false;
            lastIisCheck.Text = "Refreshing IIS inventory...";
            bool Current() => ReferenceEquals(inventoryRequest, request) && !request.IsCancellationRequested &&
                !grid.IsDisposed && !grid.Disposing && grid.Parent is not null && grid.Columns.Count == 6 &&
                !poolGrid.IsDisposed && !poolGrid.Disposing && poolGrid.Parent is not null && poolGrid.Columns.Count == 2;
            try
            {
                var inventory = await Task.Run(async () =>
                {
                    var sites = await _environment.ListIisSitesAsync(request.Token);
                    var pools = await _environment.ListIisAppPoolsAsync(request.Token);
                    return (sites, pools);
                }, request.Token);
                if (!Current()) return;
                grid.SuspendLayout();
                poolGrid.SuspendLayout();
                try
                {
                    grid.Rows.Clear();
                    foreach (var x in inventory.sites)
                    {
                        var i = grid.Rows.Add(false, x.Name, x.State, x.Bindings, x.PhysicalPath, x.AppPool);
                        grid.Rows[i].Tag = x;
                    }
                    poolGrid.Rows.Clear();
                    foreach (var ap in inventory.pools)
                    {
                        var i = poolGrid.Rows.Add(ap.Name, ap.State);
                        poolGrid.Rows[i].Tag = ap;
                    }
                    lastIisCheck.Text = "Last checked: " + DateTime.Now.ToString("HH:mm:ss");
                }
                finally { grid.ResumeLayout(); poolGrid.ResumeLayout(); }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!Current()) return;
                grid.Rows.Clear();
                poolGrid.Rows.Clear();
                lastIisCheck.Text = "IIS refresh failed";
                AppendLog("[IIS] Could not enumerate sites: " + SecretRedactor.Redact(ex.Message));
                MessageBox.Show(this, SecretRedactor.Redact(ex.Message), "IIS site list", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (ReferenceEquals(inventoryRequest, request))
                {
                    inventoryRequest = null;
                    if (!refreshSites.IsDisposed) refreshSites.Enabled = true;
                }
                request.Dispose();
            }
        }

        List<IisSiteInfo> PickedSites() => inventoryRequest is not null ? new List<IisSiteInfo>() : grid.Rows.Cast<DataGridViewRow>()
            .Where(r => Convert.ToBoolean(r.Cells["Pick"].Value ?? false) && r.Tag is IisSiteInfo)
            .Select(r => (IisSiteInfo)r.Tag!).ToList();

        install.Click += async (_, _) => await BusyAsync(install, async () => { AppendLog("Enabling IIS features with DISM..."); await _environment.InstallIisAsync(); await RefreshIisStatus(); });
        refresh.Click += async (_, _) => { await RefreshIisStatus(); if (advanced.Checked) await LoadSites(); };
        advanced.Checked = _iisState.Installed;
        advanced.CheckedChanged += async (_, _) => { if (_iisState.Installed && advanced.Checked) { management.Visible = true; await LoadSites(); } else management.Visible = false; };
        if (_iisState.Installed) BeginInvoke(new Action(async () => { if (!grid.IsDisposed && grid.Parent is not null) await LoadSites(); }));
        refreshSites.Click += async (_, _) => await LoadSites();
        IisAppPoolInfo? SelectedPool() => inventoryRequest is not null ? null : poolGrid.CurrentRow?.Tag as IisAppPoolInfo;
        startPool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; await BusyAsync(startPool, async () => { await _environment.StartIisAppPoolAsync(x.Name); await LoadSites(); }); };
        stopPool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; await BusyAsync(stopPool, async () => { await _environment.StopIisAppPoolAsync(x.Name); await LoadSites(); }); };
        recyclePool.Click += async (_, _) => { var x=SelectedPool(); if(x is null)return; await BusyAsync(recyclePool, async () => { await _environment.RecycleIisAppPoolAsync(x.Name); await LoadSites(); }); };
        deletePool.Click += async (_, _) => await BusyAsync(deletePool, async () =>
        {
            var selected = SelectedPool();
            if (selected is null) return;
            if (MessageBox.Show(this, $"Delete unused application pool '{selected.Name}'?", "Delete App Pool",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await _environment.DeleteIisAppPoolAsync(selected.Name);
            await LoadSites();
        });
        startSites.Click += async (_, _) => await BusyAsync(startSites, async () =>
        {
            foreach (var x in PickedSites()) await _environment.StartIisSiteAsync(x.Name);
            await LoadSites();
        });
        stopSites.Click += async (_, _) => await BusyAsync(stopSites, async () =>
        {
            foreach (var x in PickedSites()) await _environment.StopIisSiteAsync(x.Name);
            await LoadSites();
        });
        removeSites.Click += async (_, _) => await BusyAsync(removeSites, async () =>
        {
            var picked = PickedSites();
            if (picked.Count == 0) { MessageBox.Show(this, "Tick at least one IIS site first.", "IIS cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var list = string.Join("\r\n", picked.Select(x => $"• {x.Name} — {x.Bindings}\r\n  {x.PhysicalPath}"));
            var answer = MessageBox.Show(this,
                "The selected IIS site configuration, its unused dedicated App Pool, AND its physical site folder/files will be removed. Databases are not deleted.\r\n\r\n" + list + "\r\n\r\nContinue?",
                "Remove IIS site + cleanup files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            if (picked.Any(x => !string.IsNullOrWhiteSpace(x.PhysicalPath) && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(x.PhysicalPath.Trim('"'))).TrimEnd('\\'), @"C:\inetpub\wwwroot", StringComparison.OrdinalIgnoreCase)))
            {
                var second = MessageBox.Show(this, "C:\\inetpub\\wwwroot is included. This will delete the files currently in the IIS default web root. Confirm cleanup?", "Confirm wwwroot cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (second != DialogResult.Yes) return;
            }
            foreach (var x in picked) await _environment.RemoveIisSiteAndFilesAsync(x, deletePhysicalFiles: true);
            AppendLog($"[IIS CLEANUP] Removed {picked.Count} selected site(s) and cleaned their physical files.");
            await LoadSites();
        });
    }
}
