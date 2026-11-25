namespace GenericWeb.DeploymentManager.Services;

// Public server configuration only. Never store user credentials here.
// Presets use STARTTLS, supported by the existing SMTP transport.
internal sealed record EmailProviderPreset(string Name, string Host, int Port, string PasswordHint)
{
    public override string ToString() => Name;
}

internal static class EmailProviderPresets
{
    public static readonly EmailProviderPreset Gmail = new(
        "Gmail", "smtp.gmail.com", 587, "Use a Google App Password if your account permits it.");
    public static readonly EmailProviderPreset Yahoo = new(
        "Yahoo Mail", "smtp.mail.yahoo.com", 587, "Use a Yahoo app password if your account permits it.");
    public static readonly EmailProviderPreset ICloud = new(
        "iCloud Mail", "smtp.mail.me.com", 587, "Use an Apple app-specific password.");
    public static readonly EmailProviderPreset Custom = new(
        "Custom SMTP (company / other)", "", 587, "Enter your provider's SMTP host, STARTTLS port and credentials.");

    public static IReadOnlyList<EmailProviderPreset> All { get; } =
        Array.AsReadOnly(new[] { Gmail, Yahoo, ICloud, Custom });

    // Match the saved V266 endpoint, without rewriting previously configured
    // custom endpoints or changing the protected settings format.
    public static EmailProviderPreset FromEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) return Gmail;
        return All.FirstOrDefault(p => p != Custom &&
            p.Port == port && string.Equals(p.Host, host.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Custom;
    }
}
