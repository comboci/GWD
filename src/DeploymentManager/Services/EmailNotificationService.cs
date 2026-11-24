using Microsoft.Win32;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;

namespace GenericWeb.DeploymentManager.Services;

public sealed class EmailNotificationSettings
{
    public bool Enabled { get; set; }
    public bool OnSuccess { get; set; } = true;
    public bool OnFailure { get; set; } = true;
    public bool OnCancellation { get; set; } = true;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Sender { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Recipient { get; set; } = "";

    public bool ShouldNotify(string result) => Enabled && (result switch
    {
        "Successful" => OnSuccess,
        "Failed" or "RecoveryFailed" => OnFailure,
        "Cancelled" => OnCancellation,
        _ => false
    });
}

public static class EmailNotificationService
{
    // Store the complete configuration encrypted in HKCU, outside project
    // profiles, shared settings.json, and diagnostic ZIP source directories.
    private const string RegistryPath = @"Software\NSP\DeploymentManager";
    private const string RegistryValue = "EmailNotificationsProtected";

    public static EmailNotificationSettings Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        if (key?.GetValue(RegistryValue) is not string value || string.IsNullOrEmpty(value)) return new();
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<EmailNotificationSettings>(plain) ?? new(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Save(EmailNotificationSettings settings)
    {
        if (settings.Enabled) Validate(settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is null) throw new InvalidOperationException("Windows credential store unavailable.");
            key.SetValue(RegistryValue, Convert.ToBase64String(encrypted), RegistryValueKind.String);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Validate(EmailNotificationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || Uri.CheckHostName(settings.Host) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a valid SMTP hostname.");
        if (settings.Port is < 1 or > 65535 || settings.Port == 465)
            throw new ArgumentException("Use a STARTTLS SMTP port (normally 587); implicit TLS port 465 is not supported.");
        if (!MailAddress.TryCreate(settings.Sender, out _) || !MailAddress.TryCreate(settings.Recipient, out _))
            throw new ArgumentException("Enter a valid sender and recipient email address.");
        if (string.IsNullOrWhiteSpace(settings.Password))
            throw new ArgumentException("Enter the SMTP password or app password.");
    }

    public static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    public static async Task SendAsync(EmailNotificationSettings settings, string subject, string body)
    {
        Validate(settings);
        using var mail = new MailMessage(settings.Sender, settings.Recipient, SingleLine(subject), body);
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = true, // STARTTLS required; no insecure fallback
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(
                string.IsNullOrWhiteSpace(settings.UserName) ? new MailAddress(settings.Sender).Address : settings.UserName,
                settings.Password)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.SendMailAsync(mail, timeout.Token).ConfigureAwait(false);
    }
}
