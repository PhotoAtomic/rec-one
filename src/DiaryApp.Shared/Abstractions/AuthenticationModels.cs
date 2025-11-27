namespace DiaryApp.Shared.Abstractions;

public sealed record OidcProviderConfiguration
{
    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? CallbackPath { get; init; }
    public string? ResponseType { get; init; }

    public bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(Authority) &&
               !string.IsNullOrWhiteSpace(ClientId);
    }
}

public sealed record OidcAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public OidcProviderConfiguration Microsoft { get; init; } = new();
    public OidcProviderConfiguration Google { get; init; } = new();

    public bool AnyProviderConfigured()
    {
        return Microsoft.IsConfigured() || Google.IsConfigured();
    }
}

public sealed record AuthenticationProviderInfo(
    string Name,
    string DisplayName,
    string LoginPath);

public sealed record AvailableProvidersDto(
    bool AuthenticationEnabled,
    IReadOnlyCollection<AuthenticationProviderInfo> Providers);

public sealed record DisclaimerOptions
{
    public const string SectionName = "Disclaimer";

    public string Text { get; init; } = 
        "TECHNICAL DEMONSTRATOR - BETA VERSION\n\n" +
        "This is a technical demonstrator application currently in beta testing. " +
        "IMPORTANT NOTICES:\n\n" +
        "- This system does not follow any regulatory compliance standards\n" +
        "- NO backup mechanisms are implemented\n" +
        "- NO privacy safeguards are implemented\n" +
        "- Data loss or exposure may occur at any time\n\n" +
        "We strongly advise you NOT to use this application unless you are an authorized participant " +
        "in the beta test program.\n\n" +
        "This application is provided FREE OF CHARGE and AS-IS. By using this application, you acknowledge " +
        "that you use it AT YOUR OWN RISK. We are NOT LIABLE in any way for what happens to your data, " +
        "including but not limited to data loss, corruption, unauthorized access, or any other damages.";
}
