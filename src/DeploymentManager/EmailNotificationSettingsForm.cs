using GenericWeb.DeploymentManager.Services;

namespace GenericWeb.DeploymentManager;

public sealed class EmailNotificationSettingsForm : Form
{
    private readonly CheckBox _enabled = new() { Text = "Enable email notifications", AutoSize = true };
    private readonly CheckBox _success = new() { Text = "Success", AutoSize = true };
    private readonly CheckBox _failure = new() { Text = "Failure / recovery failure", AutoSize = true };
    private readonly CheckBox _cancelled = new() { Text = "Cancelled", AutoSize = true };
    private readonly ComboBox _provider = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _providerHint = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.DimGray };
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 110, Value = 587 };
    private readonly TextBox _sender = new() { Dock = DockStyle.Fill };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _recipient = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly Button _save = new() { Text = "Save", Width = 110, Height = 34 };
    private readonly Button _test = new() { Text = "Send Test Email", Width = 150, Height = 34 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 110, Height = 34 };
    private bool _testing;

    public EmailNotificationSettingsForm()
    {
        GwdUi.ConfigureForm(this);
        Text = "GWD — Email Notifications";
        ClientSize = new Size(760, 570);
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        var settings = EmailNotificationService.Load();
        _enabled.Checked = settings.Enabled;
        _success.Checked = settings.OnSuccess;
        _failure.Checked = settings.OnFailure;
        _cancelled.Checked = settings.OnCancellation;
        _provider.Items.AddRange(EmailProviderPresets.All.Cast<object>().ToArray());
        _provider.SelectedItem = EmailProviderPresets.FromEndpoint(settings.Host, settings.Port);
        _host.Text = settings.Host;
        _port.Value = Math.Clamp(settings.Port, 1, 65535);
        _sender.Text = settings.Sender;
        _user.Text = settings.UserName;
        _password.Text = settings.Password;
        _recipient.Text = settings.Recipient;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16), ColumnCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void AddWide(Control control)
        {
            var row = root.RowCount++;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(control, 0, row);
            root.SetColumnSpan(control, 2);
        }
        Label AddRow(string label, Control control)
        {
            var row = root.RowCount++;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            root.Controls.Add(caption, 0, row);
            control.Margin = new Padding(3, 4, 3, 7);
            root.Controls.Add(control, 1, row);
            return caption;
        }
        AddWide(_enabled);
        AddRow("Email provider", _provider);
        AddWide(_providerHint);
        var hostLabel = AddRow("SMTP server", _host);
        var portLabel = AddRow("SMTP port (STARTTLS)", _port);
        AddRow("Sender email", _sender);
        AddRow("SMTP login (optional)", _user);
        AddRow("Password / app password", _password);
        AddRow("Recipient email", _recipient);
        AddWide(new Label { Text = "Leave SMTP login blank to use the sender email address.", AutoSize = true, ForeColor = Color.DimGray });
        AddWide(new Label { Text = "Which deployment results should send an email?", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        var events = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        events.Controls.AddRange(new Control[] { _success, _failure, _cancelled });
        AddWide(events);
        AddWide(new Label { Text = "STARTTLS is required. Port 465 and OAuth-only accounts (including Outlook.com) are not supported. Credentials are protected for the current Windows user.", AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Color.DimGray, Padding = new Padding(0, 8, 0, 8) });

        void UpdateProviderFields()
        {
            var selected = SelectedProvider;
            var custom = selected == EmailProviderPresets.Custom;
            root.SuspendLayout();
            try
            {
                hostLabel.Visible = _host.Visible = custom;
                portLabel.Visible = _port.Visible = custom;
                _providerHint.Text = selected.PasswordHint;
            }
            finally { root.ResumeLayout(true); }
        }
        _provider.SelectedIndexChanged += (_, _) =>
        {
            // A password saved for one provider must not be sent to another.
            _password.Clear();
            UpdateProviderFields();
            SetStatus("Re-enter the password for the selected provider.", true);
        };
        UpdateProviderFields();
        AddWide(_status);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { _save, _cancel, _test });
        AddWide(buttons);
        Controls.Add(root);
        GwdUi.StyleTree(this);
        GwdUi.ButtonStyle(_save, primary: true);
        _save.Click += (_, _) => Save();
        _test.Click += async (_, _) => await TestAsync();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = _cancel;
        FormClosing += (_, e) => { if (_testing) e.Cancel = true; };
        FormClosed += (_, _) => _password.Clear();
    }

    private EmailProviderPreset SelectedProvider =>
        _provider.SelectedItem as EmailProviderPreset ?? EmailProviderPresets.Gmail;

    private EmailNotificationSettings Read() => new()
    {
        Enabled = _enabled.Checked,
        OnSuccess = _success.Checked,
        OnFailure = _failure.Checked,
        OnCancellation = _cancelled.Checked,
        Host = SelectedProvider == EmailProviderPresets.Custom ? _host.Text.Trim() : SelectedProvider.Host,
        Port = SelectedProvider == EmailProviderPresets.Custom ? (int)_port.Value : SelectedProvider.Port,
        Sender = _sender.Text.Trim(),
        UserName = _user.Text.Trim(),
        Password = _password.Text,
        Recipient = _recipient.Text.Trim()
    };

    private void Save()
    {
        var settings = Read();
        if (settings.Enabled && !settings.OnSuccess && !settings.OnFailure && !settings.OnCancellation)
        {
            SetStatus("Select at least one deployment result, or disable notifications.", false);
            return;
        }
        try { if (settings.Enabled) EmailNotificationService.Validate(settings); }
        catch (ArgumentException ex) { SetStatus(ex.Message, false); return; }
        try
        {
            EmailNotificationService.Save(settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception) { SetStatus("Could not save protected email settings for this Windows user.", false); }
    }

    private async Task TestAsync()
    {
        if (_testing) return;
        var settings = Read();
        try { EmailNotificationService.Validate(settings); }
        catch (ArgumentException ex) { SetStatus(ex.Message, false); return; }
        _testing = true;
        _test.Enabled = _save.Enabled = _cancel.Enabled = _provider.Enabled = false;
        SetStatus("Sending test email (up to 15 seconds)...", true);
        try
        {
            await EmailNotificationService.SendAsync(settings, "[GWD] Test notification",
                "This is a test from GWD. SMTP accepted this message if delivery succeeds. No deployment was started.");
            SetStatus("Test email accepted by SMTP server. Check the recipient inbox and spam folder.", true);
        }
        catch (Exception)
        {
            SetStatus("Test failed. Check SMTP host, STARTTLS port, login, app password and network access. Settings were not saved by this test.", false);
        }
        finally
        {
            _testing = false;
            _test.Enabled = _save.Enabled = _cancel.Enabled = _provider.Enabled = true;
        }
    }

    private void SetStatus(string message, bool ok)
    {
        _status.Text = message;
        _status.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
    }
}
