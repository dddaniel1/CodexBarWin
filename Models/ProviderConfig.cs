namespace CodexBarWin.Models;

/// <summary>
/// Configuration for a provider (user settings only).
/// </summary>
public record ProviderConfig
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? IconPath { get; init; }
    public int Order { get; set; }
    public string? AuthSecret { get; set; }


    /// <summary>
    /// Gets the default providers.
    /// </summary>
    public static IReadOnlyList<ProviderConfig> GetDefaults()
        => ProviderConstants.ProviderDefinitions
            .Select((provider, index) => new ProviderConfig
            {
                Id = provider.Id,
                DisplayName = provider.DisplayName,
                IsEnabled = provider.DefaultEnabled,
                Order = index
            })
            .ToList();
}

/// <summary>
/// Application constants for providers (code-managed, not user settings).
/// </summary>
public static class ProviderConstants
{
    public sealed record ProviderDefinition(
        string Id,
        string DisplayName,
        string Source,
        bool DefaultEnabled = false);

    /// <summary>
    /// Provider catalog aligned with CodexBar providers.
    /// </summary>
    public static readonly IReadOnlyList<ProviderDefinition> ProviderDefinitions =
    [
        new("claude", "Claude", "auto", true),
        new("codex", "Codex", "auto", true),
        new("gemini", "Gemini", "auto", true),
        new("cursor", "Cursor", "auto", true),
        new("antigravity", "Antigravity", "cli", true),
        new("opencode", "OpenCode", "auto"),
        new("opencodego", "OpenCode Go", "auto"),
        new("alibaba", "Alibaba Coding Plan", "auto"),
        new("factory", "Droid (Factory)", "auto"),
        new("copilot", "Copilot", "api"),
        new("zai", "z.ai", "api"),
        new("minimax", "MiniMax", "auto"),
        new("kimi", "Kimi", "api"),
        new("kilo", "Kilo", "auto"),
        new("kiro", "Kiro", "cli"),
        new("vertexai", "Vertex AI", "oauth"),
        new("augment", "Augment", "auto"),
        new("jetbrains", "JetBrains AI", "auto"),
        new("kimik2", "Kimi K2", "api"),
        new("amp", "Amp", "web"),
        new("ollama", "Ollama", "web"),
        new("synthetic", "Synthetic", "api"),
        new("warp", "Warp", "api"),
        new("openrouter", "OpenRouter", "api"),
        new("perplexity", "Perplexity", "web"),
        new("xingchen", "讯飞星辰", "api")
    ];


    private static readonly IReadOnlyDictionary<string, ProviderDefinition> ProviderDefinitionMap =
        ProviderDefinitions.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set of allowed provider IDs for security validation.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedProviders =
        new HashSet<string>(ProviderDefinitions.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a provider ID is valid (exists in the allowed list).
    /// </summary>
    public static bool IsValidProvider(string providerId)
        => !string.IsNullOrWhiteSpace(providerId) && AllowedProviders.Contains(providerId);

    /// <summary>
    /// Validates and normalizes a provider ID.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the provider ID is invalid.</exception>
    public static string ValidateAndNormalize(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID cannot be null or empty.", nameof(providerId));

        var normalized = providerId.Trim().ToLowerInvariant();
        if (!AllowedProviders.Contains(normalized))
            throw new ArgumentException($"Invalid provider: '{providerId}'", nameof(providerId));

        return normalized;
    }

    /// <summary>
    /// Gets the source type for fetching usage data.
    /// This is determined by CodexBar provider strategy defaults and should not be user-configurable.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the provider ID is invalid.</exception>
    public static string GetSource(string providerId)
    {
        var normalized = ValidateAndNormalize(providerId);
        return ProviderDefinitionMap[normalized].Source;
    }

    /// <summary>
    /// Gets the user-facing provider name.
    /// </summary>
    public static string GetDisplayName(string providerId)
    {
        var normalized = ValidateAndNormalize(providerId);
        return ProviderDefinitionMap[normalized].DisplayName;
    }
}
