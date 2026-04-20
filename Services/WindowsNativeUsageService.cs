using System.Net.Http.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexBarWin.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Fetches AI provider usage data directly via HTTP APIs without any CLI dependency.
/// Supports Codex (OpenAI), Claude (Anthropic), Gemini (Google), and Cursor.
/// </summary>
public class WindowsNativeUsageService : ICodexBarService
{
    private readonly ICacheService _cacheService;
    private readonly ISettingsService _settingsService;
    private readonly ISampleDataLoader _sampleDataLoader;
    private readonly ILogger<WindowsNativeUsageService> _logger;
    private readonly HttpClient _httpClient;

    private static readonly string HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly IReadOnlySet<string> NativeProviders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "codex", "claude", "gemini", "cursor", "antigravity",
            "zai", "kimik2", "openrouter", "warp", "copilot",
            "jetbrains", "kiro", "opencode", "opencodego", "kimi", "amp", "ollama", "perplexity", "xingchen"
        };

    public WindowsNativeUsageService(
        ICacheService cacheService,
        ISettingsService settingsService,
        ISampleDataLoader sampleDataLoader,
        ILogger<WindowsNativeUsageService> logger)
    {
        _cacheService = cacheService;
        _settingsService = settingsService;
        _sampleDataLoader = sampleDataLoader;
        _logger = logger;

        _httpClient = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    // ─── ICodexBarService ─────────────────────────────────────────────────

    public async Task<UsageData?> GetUsageAsync(string provider, CancellationToken ct = default)
    {
        try
        {
            var data = await FetchProviderAsync(provider, ct);
            if (data.Error == null)
                _cacheService.Set(data.Provider, data);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetUsageAsync failed for {Provider}", provider);
            return _cacheService.Get(provider);
        }
    }

    public async Task<IReadOnlyList<UsageData>> GetAllUsageAsync(CancellationToken ct = default)
    {
        var providers = GetEnabledProviders();
        var results = new List<UsageData>();
        foreach (var id in providers)
            results.Add(await FetchProviderAsync(id, ct));
        Interlocked.Exchange(ref _isFirstFetch, 0);
        return results;
    }

    public async IAsyncEnumerable<UsageData> GetAllUsageStreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var providers = GetEnabledProviders();
        if (providers.Count == 0) yield break;

        var tasks = providers.Select(id => FetchProviderAsync(id, ct)).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            yield return await done;
        }

        Interlocked.Exchange(ref _isFirstFetch, 0);
    }

    public Task<string?> GetVersionAsync(CancellationToken ct = default)
        => Task.FromResult<string?>("Native HTTP + CodexBar CLI fallback");

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    // ─── Internal helpers ─────────────────────────────────────────────────

    private int _isFirstFetch = 1;

    private List<string> GetEnabledProviders() =>
        _settingsService.Settings.Providers
            .Where(p => p.IsEnabled && ProviderConstants.IsValidProvider(p.Id))
            .OrderBy(p => p.Order)
            .Select(p => p.Id)
            .ToList();

    private async Task<UsageData> FetchProviderAsync(string provider, CancellationToken ct)
    {
        try
        {
            var normalized = ProviderConstants.ValidateAndNormalize(provider);

            // Developer mode: load sample data
            if (_settingsService.Settings.DeveloperModeEnabled)
            {
                var sampleJson = _sampleDataLoader.LoadSampleJson(normalized);
                if (!string.IsNullOrWhiteSpace(sampleJson))
                {
                    var dto = JsonSerializer.Deserialize(sampleJson, AppJsonSerializerContext.Default.UsageDataDto);
                    if (dto != null)
                    {
                        var d = dto.ToUsageData();
                        _cacheService.Set(d.Provider, d);
                        return d;
                    }
                }
                return ErrorData(provider, "Sample data not available (Developer mode)");
            }

            var isFirst = Interlocked.CompareExchange(ref _isFirstFetch, 1, 1) == 1;
            
            // Determine if this is a CLI-based fetch (either native CLI provider or fallback to codexbar CLI)
            var source = ProviderConstants.GetSource(normalized);
            var isCliFetch = source.Equals("cli", StringComparison.OrdinalIgnoreCase) || !NativeProviders.Contains(normalized);

            var timeout = isFirst
                ? TimeSpan.FromSeconds(isCliFetch 
                    ? _settingsService.Settings.Timeouts.CliProviderFirstFetchTimeoutSeconds 
                    : _settingsService.Settings.Timeouts.StandardProviderFirstFetchTimeoutSeconds)
                : TimeSpan.FromSeconds(isCliFetch 
                    ? _settingsService.Settings.Timeouts.CliProviderTimeoutSeconds 
                    : _settingsService.Settings.Timeouts.StandardProviderTimeoutSeconds);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            UsageData result = NativeProviders.Contains(normalized)
                ? normalized switch
            {
                "codex" => await FetchCodexAsync(cts.Token),
                "claude" => await FetchClaudeAsync(cts.Token),
                "gemini" => await FetchGeminiAsync(cts.Token),
                "cursor" => await FetchCursorAsync(cts.Token),
                "antigravity" => await FetchAntigravityAsync(cts.Token),
                "zai" => await FetchZaiAsync(cts.Token),
                "kimik2" => await FetchKimiK2Async(cts.Token),
                "openrouter" => await FetchOpenRouterAsync(cts.Token),
                "warp" => await FetchWarpAsync(cts.Token),
                "copilot" => await FetchCopilotAsync(cts.Token),
                "jetbrains" => await FetchJetBrainsAsync(cts.Token),
                "kiro" => await FetchKiroAsync(cts.Token),
                "opencode" => await FetchOpenCodeAsync(cts.Token),
                "opencodego" => await FetchOpenCodeGoAsync(cts.Token),
                "kimi" => await FetchKimiAsync(cts.Token),
                "amp" => await FetchAmpAsync(cts.Token),
                "ollama" => await FetchOllamaAsync(cts.Token),
                "perplexity" => await FetchPerplexityAsync(cts.Token),
                "xingchen" => await FetchXingchenAsync(cts.Token),
                _ => ErrorData(provider, $"Provider '{provider}' is unavailable")
            }
                : await FetchViaCliOrErrorAsync(provider, normalized, cts.Token);

            if (result.Error == null)
                _cacheService.Set(result.Provider, result);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{Provider} timed out", provider);
            return _cacheService.Get(provider) ?? ErrorData(provider, "Request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Provider}", provider);
            return _cacheService.Get(provider) ?? ErrorData(provider, ex.Message);
        }
    }

    private static UsageData ErrorData(string provider, string error) =>
        new() { Provider = provider, Error = error, FetchedAt = DateTime.UtcNow };

    private async Task<UsageData> FetchViaCliOrErrorAsync(string provider, string normalized, CancellationToken ct)
    {
        var cliData = await TryFetchViaCodexBarCliAsync(normalized, ct);
        if (cliData != null)
        {
            _cacheService.Set(cliData.Provider, cliData);
            return cliData;
        }

        return ErrorData(provider, $"Provider '{provider}' requires codexbar CLI. Install and configure codexbar to query this provider.");
    }

    private async Task<UsageData?> TryFetchViaCodexBarCliAsync(string provider, CancellationToken ct)
    {
        var source = ProviderConstants.GetSource(provider);
        var sourceFlag = source.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" --source {source}";

        var command = $"codexbar --provider {provider} --format json --json-only{sourceFlag}";
        var result = await RunPowerShellCommandAsync(command, ct);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            _logger.LogDebug(
                "codexbar CLI fetch failed for {Provider}. Exit={ExitCode}, Error={Error}",
                provider,
                result.ExitCode,
                string.IsNullOrWhiteSpace(result.Error) ? "<none>" : result.Error.Trim());
            return null;
        }

        var parsed = TryParseUsageDataJson(result.Output, provider);
        if (parsed == null)
        {
            _logger.LogDebug("codexbar CLI returned unparsable JSON for {Provider}", provider);
            return null;
        }

        return parsed with { Provider = provider };
    }

    private static UsageData? TryParseUsageDataJson(string stdout, string provider)
    {
        var trimmed = stdout.TrimStart();

        if (trimmed.StartsWith('['))
        {
            var dtos = JsonSerializer.Deserialize(trimmed, AppJsonSerializerContext.Default.ListUsageDataDto);
            return dtos?
                .Select(d => d.ToUsageData())
                .FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                ?? dtos?.Select(d => d.ToUsageData()).FirstOrDefault();
        }

        if (trimmed.StartsWith('{'))
        {
            var dto = JsonSerializer.Deserialize(trimmed, AppJsonSerializerContext.Default.UsageDataDto);
            return dto?.ToUsageData();
        }

        return null;
    }

    private static async Task<(bool Success, string Output, string Error, int ExitCode)> RunPowerShellCommandAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(true); } catch { /* Ignore */ }
                }
                throw;
            }

            return (process.ExitCode == 0, await outputTask, await errorTask, process.ExitCode);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message, -1);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FIRST WAVE NATIVE PROVIDERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<UsageData> FetchZaiAsync(CancellationToken ct)
    {
        var apiKey = GetProviderApiKey("zai", ["Z_AI_API_KEY"]);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ErrorData("zai", "z.ai API token not found. Configure ~/.codexbar/config.json or Z_AI_API_KEY.");

        var quotaUrl = GetZaiQuotaUrl();
        using var req = new HttpRequestMessage(HttpMethod.Get, quotaUrl);
        req.Headers.TryAddWithoutValidation("authorization", $"Bearer {apiKey}");
        req.Headers.TryAddWithoutValidation("accept", "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataEl) ? dataEl : root;
        var plan = FirstNonEmptyString(data, "planName", "plan", "plan_type", "packageName");

        JsonElement? tokenLimit = null;
        JsonElement? timeLimit = null;
        JsonElement? shortTokenLimit = null;

        if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                var type = limit.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (string.Equals(type, "TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokenLimit == null)
                    {
                        tokenLimit = limit;
                    }
                    else
                    {
                        var currentMinutes = GetWindowMinutes(tokenLimit.Value);
                        var nextMinutes = GetWindowMinutes(limit);
                        if (nextMinutes.HasValue && (!currentMinutes.HasValue || nextMinutes.Value < currentMinutes.Value))
                        {
                            shortTokenLimit = limit;
                        }
                        else
                        {
                            shortTokenLimit = tokenLimit;
                            tokenLimit = limit;
                        }
                    }
                }
                else if (string.Equals(type, "TIME_LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    timeLimit = limit;
                }
            }
        }

        return new UsageData
        {
            Provider = "zai",
            Plan = string.IsNullOrWhiteSpace(plan) ? "z.ai" : plan,
            Session = tokenLimit.HasValue ? JsonLimitToUsageWindow(tokenLimit.Value) : null,
            Weekly = timeLimit.HasValue ? JsonLimitToUsageWindow(timeLimit.Value) : null,
            Tertiary = shortTokenLimit.HasValue ? JsonLimitToUsageWindow(shortTokenLimit.Value) : null,
            Status = "api",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchKimiK2Async(CancellationToken ct)
    {
        var apiKey = GetProviderApiKey("kimik2", ["KIMI_K2_API_KEY", "KIMI_API_KEY", "KIMI_KEY"]);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ErrorData("kimik2", "Kimi K2 API key not found. Configure ~/.codexbar/config.json or KIMI_K2_API_KEY.");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://kimi-k2.ai/api/user/credits");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var consumed = FindDouble(root,
            ["total_credits_consumed"], ["totalCreditsConsumed"], ["total_credits_used"], ["totalCreditsUsed"],
            ["credits_consumed"], ["creditsConsumed"], ["consumedCredits"], ["usedCredits"], ["total"],
            ["usage", "total"], ["usage", "consumed"]) ?? 0;
        var remaining = FindDouble(root,
            ["credits_remaining"], ["creditsRemaining"], ["remaining_credits"], ["remainingCredits"],
            ["available_credits"], ["availableCredits"], ["credits_left"], ["creditsLeft"],
            ["usage", "credits_remaining"], ["usage", "remaining"])
            ?? TryGetHeaderDouble(resp, "x-credits-remaining")
            ?? 0;

        return new UsageData
        {
            Provider = "kimik2",
            Plan = "Kimi K2",
            Session = BuildPercentWindow(consumed, consumed + Math.Max(0, remaining), null),
            Credits = new CreditsInfo
            {
                Used = (decimal)consumed,
                Limit = (decimal)(consumed + Math.Max(0, remaining))
            },
            Status = "api",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchOpenRouterAsync(CancellationToken ct)
    {
        var apiKey = GetProviderApiKey("openrouter", ["OPENROUTER_API_KEY"]);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ErrorData("openrouter", "OpenRouter API key not found. Configure ~/.codexbar/config.json or OPENROUTER_API_KEY.");

        var baseUrl = GetEnvironmentTrimmed("OPENROUTER_API_URL") ?? "https://openrouter.ai/api/v1";
        var creditsUrl = baseUrl.TrimEnd('/') + "/credits";
        var keyUrl = baseUrl.TrimEnd('/') + "/key";

        using var creditsReq = new HttpRequestMessage(HttpMethod.Get, creditsUrl);
        AddOpenRouterHeaders(creditsReq, apiKey);
        var creditsResp = await _httpClient.SendAsync(creditsReq, ct);
        creditsResp.EnsureSuccessStatusCode();

        using var creditsDoc = JsonDocument.Parse(await creditsResp.Content.ReadAsStringAsync(ct));
        var creditsData = creditsDoc.RootElement.GetProperty("data");
        var totalCredits = creditsData.GetProperty("total_credits").GetDouble();
        var totalUsage = creditsData.GetProperty("total_usage").GetDouble();

        double? keyLimit = null;
        double? keyUsage = null;
        try
        {
            using var keyReq = new HttpRequestMessage(HttpMethod.Get, keyUrl);
            AddOpenRouterHeaders(keyReq, apiKey);
            var keyResp = await _httpClient.SendAsync(keyReq, ct);
            if (keyResp.IsSuccessStatusCode)
            {
                using var keyDoc = JsonDocument.Parse(await keyResp.Content.ReadAsStringAsync(ct));
                if (keyDoc.RootElement.TryGetProperty("data", out var keyData))
                {
                    keyLimit = keyData.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind is JsonValueKind.Number
                        ? limitEl.GetDouble()
                        : null;
                    keyUsage = keyData.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind is JsonValueKind.Number
                        ? usageEl.GetDouble()
                        : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OpenRouter key quota fetch failed");
        }

        return new UsageData
        {
            Provider = "openrouter",
            Plan = "OpenRouter",
            Session = keyLimit.HasValue && keyUsage.HasValue && keyLimit.Value > 0
                ? BuildPercentWindow(keyUsage.Value, keyLimit.Value, null)
                : null,
            Credits = new CreditsInfo
            {
                Used = (decimal)totalUsage,
                Limit = (decimal)totalCredits
            },
            Status = "api",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchWarpAsync(CancellationToken ct)
    {
        var apiKey = GetProviderApiKey("warp", ["WARP_API_KEY", "WARP_TOKEN"]);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ErrorData("warp", "Warp API key not found. Configure ~/.codexbar/config.json or WARP_API_KEY.");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://app.warp.dev/graphql/v2?op=GetRequestLimitInfo");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "Warp/1.0");
        req.Headers.TryAddWithoutValidation("x-warp-client-id", "warp-app");
        req.Headers.TryAddWithoutValidation("x-warp-os-category", "Windows");
        req.Headers.TryAddWithoutValidation("x-warp-os-name", "Windows");
        req.Headers.TryAddWithoutValidation("x-warp-os-version", Environment.OSVersion.Version.ToString());
        req.Content = new StringContent("{\"query\":\"query GetRequestLimitInfo($requestContext: RequestContext!) { user(requestContext: $requestContext) { __typename ... on UserOutput { user { requestLimitInfo { isUnlimited nextRefreshTime requestLimit requestsUsedSinceLastRefresh } bonusGrants { requestCreditsGranted requestCreditsRemaining expiration } workspaces { bonusGrantsInfo { grants { requestCreditsGranted requestCreditsRemaining expiration } } } } } } }\",\"variables\":{\"requestContext\":{\"clientContext\":{},\"osContext\":{\"category\":\"Windows\",\"name\":\"Windows\",\"version\":\"" + Environment.OSVersion.Version + "\"}}},\"operationName\":\"GetRequestLimitInfo\"}", Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var user = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("user");
        var info = user.GetProperty("requestLimitInfo");
        var requestLimit = info.TryGetProperty("requestLimit", out var requestLimitEl) ? requestLimitEl.GetInt32() : 0;
        var requestsUsed = info.TryGetProperty("requestsUsedSinceLastRefresh", out var usedEl) ? usedEl.GetInt32() : 0;
        var isUnlimited = info.TryGetProperty("isUnlimited", out var unlimitedEl) && unlimitedEl.GetBoolean();
        var nextRefresh = info.TryGetProperty("nextRefreshTime", out var nextEl) ? ParseIso(nextEl.GetString()) : null;

        var (bonusGranted, bonusRemaining, bonusExpiry) = ParseWarpBonusCredits(user);

        return new UsageData
        {
            Provider = "warp",
            Plan = isUnlimited ? "Warp Unlimited" : "Warp",
            Session = isUnlimited ? null : new UsageWindow { Used = requestsUsed, Limit = Math.Max(requestLimit, 1), ResetAt = nextRefresh },
            Weekly = bonusGranted > 0 ? new UsageWindow
            {
                Used = Math.Max(0, bonusGranted - bonusRemaining),
                Limit = bonusGranted,
                ResetAt = bonusExpiry
            } : null,
            Status = "api",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchCopilotAsync(CancellationToken ct)
    {
        var token = GetProviderApiKey("copilot", ["GITHUB_TOKEN", "GH_TOKEN", "COPILOT_API_KEY"]);
        if (string.IsNullOrWhiteSpace(token))
            return ErrorData("copilot", "Copilot GitHub OAuth token not found. Configure ~/.codexbar/config.json for provider 'copilot' or set GITHUB_TOKEN.");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
        req.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
        req.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        req.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
        req.Headers.TryAddWithoutValidation("X-Github-Api-Version", "2025-04-01");

        var resp = await _httpClient.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return ErrorData("copilot", "Copilot token is invalid or expired.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var quotaSnapshots = root.TryGetProperty("quotaSnapshots", out var snapshots) ? snapshots : default;
        var premium = quotaSnapshots.ValueKind == JsonValueKind.Object && quotaSnapshots.TryGetProperty("premiumInteractions", out var premiumEl)
            ? CopilotQuotaToUsageWindow(premiumEl)
            : null;
        var chat = quotaSnapshots.ValueKind == JsonValueKind.Object && quotaSnapshots.TryGetProperty("chat", out var chatEl)
            ? CopilotQuotaToUsageWindow(chatEl)
            : null;
        var plan = root.TryGetProperty("copilotPlan", out var planEl) ? planEl.GetString() : null;

        if (premium == null && chat == null)
            return ErrorData("copilot", "Copilot usage response did not include quota snapshots.");

        return new UsageData
        {
            Provider = "copilot",
            Plan = string.IsNullOrWhiteSpace(plan) ? "Copilot" : $"Copilot {char.ToUpperInvariant(plan![0])}{plan[1..]}",
            Session = premium,
            Weekly = chat,
            Status = "api",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchJetBrainsAsync(CancellationToken ct)
    {
        await Task.CompletedTask;

        var quotaFile = FindJetBrainsQuotaFile();
        if (quotaFile == null)
            return ErrorData("jetbrains", "JetBrains AI quota file not found. Use a JetBrains IDE with AI Assistant enabled.");

        var xml = await File.ReadAllTextAsync(quotaFile, ct);
        var quotaInfoRaw = ExtractXmlOptionValue(xml, "quotaInfo");
        if (string.IsNullOrWhiteSpace(quotaInfoRaw))
            return ErrorData("jetbrains", "JetBrains quotaInfo is missing in AIAssistantQuotaManager2.xml.");

        var refillInfoRaw = ExtractXmlOptionValue(xml, "nextRefill");

        using var quotaDoc = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(quotaInfoRaw));
        var quotaRoot = quotaDoc.RootElement;
        var used = quotaRoot.TryGetProperty("current", out var currentEl) && double.TryParse(currentEl.GetString(), out var usedVal)
            ? usedVal : 0;
        var maximum = quotaRoot.TryGetProperty("maximum", out var maxEl) && double.TryParse(maxEl.GetString(), out var maxVal)
            ? maxVal : 0;
        var quotaType = quotaRoot.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        DateTime? refillAt = null;
        if (!string.IsNullOrWhiteSpace(refillInfoRaw))
        {
            using var refillDoc = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(refillInfoRaw));
            if (refillDoc.RootElement.TryGetProperty("next", out var nextEl))
                refillAt = ParseIso(nextEl.GetString());
        }

        return new UsageData
        {
            Provider = "jetbrains",
            Plan = string.IsNullOrWhiteSpace(quotaType) ? "JetBrains AI" : $"JetBrains AI ({quotaType})",
            Session = BuildPercentWindow(used, Math.Max(maximum, 1), refillAt),
            Status = "local",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchKiroAsync(CancellationToken ct)
    {
        var whoami = await RunPowerShellCommandAsync("kiro-cli whoami", ct);
        var whoamiText = $"{whoami.Output}\n{whoami.Error}".ToLowerInvariant();
        if (!whoami.Success && (whoamiText.Contains("not logged in") || whoamiText.Contains("login required")))
            return ErrorData("kiro", "Not logged in to Kiro. Run 'kiro-cli login' first.");

        var result = await RunPowerShellCommandAsync("kiro-cli chat --no-interactive \"/usage\"", ct);
        var output = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
        if (string.IsNullOrWhiteSpace(output))
            return ErrorData("kiro", "kiro-cli returned no output.");

        var stripped = StripAnsi(output);
        if (stripped.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            return ErrorData("kiro", "Not logged in to Kiro. Run 'kiro-cli login' first.");

        var plan = RegexCapture(stripped, @"\|\s*([A-Z ]+)\s*\┃?") ?? RegexCapture(stripped, @"\|\s*([A-Z ]+)\s*$");
        var usedPercent = RegexCaptureDouble(stripped, @"([0-9]+(?:\.[0-9]+)?)%") ?? 0;
        var resetAt = ParseMonthDayReset(stripped);
        var bonusUsed = RegexCaptureDouble(stripped, @"Bonus credits:[\s\S]*?([0-9]+(?:\.[0-9]+)?)\s*/\s*([0-9]+(?:\.[0-9]+)?)\s*credits used", 1);
        var bonusTotal = RegexCaptureDouble(stripped, @"Bonus credits:[\s\S]*?([0-9]+(?:\.[0-9]+)?)\s*/\s*([0-9]+(?:\.[0-9]+)?)\s*credits used", 2);
        var bonusDays = RegexCaptureInt(stripped, @"expires in\s+([0-9]+)\s+days");

        return new UsageData
        {
            Provider = "kiro",
            Plan = string.IsNullOrWhiteSpace(plan) ? "Kiro" : plan.Trim(),
            Session = new UsageWindow { Used = (int)Math.Round(usedPercent), Limit = 100, ResetAt = resetAt },
            Weekly = bonusUsed.HasValue && bonusTotal.HasValue
                ? new UsageWindow
                {
                    Used = (int)Math.Round(bonusUsed.Value),
                    Limit = Math.Max((int)Math.Round(bonusTotal.Value), 1),
                    ResetAt = bonusDays.HasValue ? DateTime.UtcNow.AddDays(bonusDays.Value) : null
                }
                : null,
            Status = "cli",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchOpenCodeAsync(CancellationToken ct)
    {
        return await FetchOpenCodeFamilyAsync("opencode", "https://opencode.ai", "https://opencode.ai/_server", true, ct);
    }

    private async Task<UsageData> FetchOpenCodeGoAsync(CancellationToken ct)
    {
        return await FetchOpenCodeFamilyAsync("opencodego", "https://opencode.ai", "https://opencode.ai/_server", false, ct);
    }

    private async Task<UsageData> FetchKimiAsync(CancellationToken ct)
    {
        var authToken = GetEnvironmentTrimmed("KIMI_AUTH_TOKEN");
        if (string.IsNullOrWhiteSpace(authToken))
        {
            var cookieHeader = GetBrowserCookieHeader(["www.kimi.com", "kimi.com"]);
            authToken = ExtractCookieValue(cookieHeader, "kimi-auth");
        }
        if (string.IsNullOrWhiteSpace(authToken))
            return ErrorData("kimi", "Kimi auth token is missing. Set KIMI_AUTH_TOKEN or log in to kimi.com in Chrome/Edge.");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authToken}");
        req.Headers.TryAddWithoutValidation("Cookie", $"kimi-auth={authToken}");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.kimi.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.kimi.com/code/console");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("x-language", "en-US");
        req.Headers.TryAddWithoutValidation("x-msh-platform", "web");
        req.Content = new StringContent("{\"scope\":[\"FEATURE_CODING\"]}", Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return ErrorData("kimi", "Kimi auth token is invalid or expired.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var usages = doc.RootElement.GetProperty("usages");
        JsonElement? coding = null;
        foreach (var usage in usages.EnumerateArray())
        {
            if (usage.TryGetProperty("scope", out var scopeEl) && scopeEl.GetString() == "FEATURE_CODING")
            {
                coding = usage;
                break;
            }
        }
        if (!coding.HasValue)
            return ErrorData("kimi", "Kimi usage response did not contain FEATURE_CODING scope.");

        var weeklyDetail = coding.Value.GetProperty("detail");
        var rateLimitDetail = coding.Value.TryGetProperty("limits", out var limitsEl) && limitsEl.ValueKind == JsonValueKind.Array && limitsEl.GetArrayLength() > 0
            ? limitsEl[0].GetProperty("detail")
            : (JsonElement?)null;

        return new UsageData
        {
            Provider = "kimi",
            Plan = "Kimi",
            Session = rateLimitDetail.HasValue ? BuildWindowFromStringUsageDetail(rateLimitDetail.Value) : null,
            Weekly = BuildWindowFromStringUsageDetail(weeklyDetail),
            Status = "api",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchAmpAsync(CancellationToken ct)
    {
        var html = await FetchHtmlWithProviderCookiesAsync("https://ampcode.com/settings", ["ampcode.com", "www.ampcode.com"], ct);
        if (html == null)
            return ErrorData("amp", "No Amp session cookie found. Log in to ampcode.com in Chrome or Edge.");

        var freeTierObject = ExtractJsObject(html, "freeTierUsage") ?? ExtractJsObject(html, "getFreeTierUsage");
        if (string.IsNullOrWhiteSpace(freeTierObject))
            return ErrorData("amp", html.Contains("sign in", StringComparison.OrdinalIgnoreCase) ? "Not logged in to Amp." : "Could not parse Amp usage data.");

        var quota = RegexCaptureDouble(freeTierObject, @"\bquota\b\s*:\s*([0-9]+(?:\.[0-9]+)?)") ?? 0;
        var used = RegexCaptureDouble(freeTierObject, @"\bused\b\s*:\s*([0-9]+(?:\.[0-9]+)?)") ?? 0;
        return new UsageData
        {
            Provider = "amp",
            Plan = "Amp",
            Session = BuildPercentWindow(used, Math.Max(quota, 1), null),
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchOllamaAsync(CancellationToken ct)
    {
        var html = await FetchHtmlWithProviderCookiesAsync("https://ollama.com/settings", ["ollama.com", "www.ollama.com"], ct);
        if (html == null)
            return ErrorData("ollama", "No Ollama session cookie found. Log in to ollama.com in Chrome or Edge.");

        var plan = RegexCapture(html, @"Cloud Usage\s*</span>\s*<span[^>]*>([^<]+)</span>");
        var sessionPercent = ParseUsageBlockPercent(html, "Session usage") ?? ParseUsageBlockPercent(html, "Hourly usage");
        var weeklyPercent = ParseUsageBlockPercent(html, "Weekly usage");
        var sessionReset = ParseUsageBlockDate(html, "Session usage") ?? ParseUsageBlockDate(html, "Hourly usage");
        var weeklyReset = ParseUsageBlockDate(html, "Weekly usage");
        if (!sessionPercent.HasValue && !weeklyPercent.HasValue)
            return ErrorData("ollama", html.Contains("sign in", StringComparison.OrdinalIgnoreCase) ? "Not logged in to Ollama." : "Could not parse Ollama usage data.");

        return new UsageData
        {
            Provider = "ollama",
            Plan = string.IsNullOrWhiteSpace(plan) ? "Ollama" : plan.Trim(),
            Session = sessionPercent.HasValue ? new UsageWindow { Used = (int)Math.Round(sessionPercent.Value), Limit = 100, ResetAt = sessionReset } : null,
            Weekly = weeklyPercent.HasValue ? new UsageWindow { Used = (int)Math.Round(weeklyPercent.Value), Limit = 100, ResetAt = weeklyReset } : null,
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<UsageData> FetchPerplexityAsync(CancellationToken ct)
    {
        var cookieHeader = GetBrowserCookieHeader(["www.perplexity.ai", "perplexity.ai"]);
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return ErrorData("perplexity", "No Perplexity session cookie found. Log in to perplexity.ai in Chrome or Edge.");

        var token = ExtractCookieValue(cookieHeader, "__Secure-authjs.session-token")
            ?? ExtractCookieValue(cookieHeader, "authjs.session-token")
            ?? ExtractCookieValue(cookieHeader, "__Secure-next-auth.session-token")
            ?? ExtractCookieValue(cookieHeader, "next-auth.session-token");
        if (string.IsNullOrWhiteSpace(token))
            return ErrorData("perplexity", "Perplexity session cookie missing or unsupported.");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.perplexity.ai/rest/billing/credits?version=2.18&source=default");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Cookie", $"__Secure-next-auth.session-token={token}");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.perplexity.ai");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.perplexity.ai/account/usage");

        var resp = await _httpClient.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return ErrorData("perplexity", "Perplexity session cookie expired.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var balance = FindDouble(root, ["balance_cents"], ["balanceCents"]) ?? 0;
        var totalUsage = FindDouble(root, ["total_usage_cents"], ["totalUsageCents"]) ?? 0;
        var renewalTs = FindDouble(root, ["renewal_date_ts"], ["renewalDateTs"]);
        var renewalDate = renewalTs.HasValue ? DateTimeOffset.FromUnixTimeSeconds((long)renewalTs.Value).UtcDateTime : (DateTime?)null;

        return new UsageData
        {
            Provider = "perplexity",
            Plan = balance > 0 ? (balance < 5000 ? "Perplexity Pro" : "Perplexity Max") : "Perplexity",
            Session = BuildPercentWindow(totalUsage, Math.Max(totalUsage + balance, 1), renewalDate),
            Credits = new CreditsInfo
            {
                Used = (decimal)(totalUsage / 100.0),
                Limit = (decimal)((totalUsage + balance) / 100.0)
            },
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CODEX (OpenAI)
    // ═══════════════════════════════════════════════════════════════════════

    private const string CodexBaseUrl = "https://chatgpt.com/backend-api";
    private const string CodexUsagePath = "/wham/usage";

    private async Task<UsageData> FetchCodexAsync(CancellationToken ct)
    {
        var creds = LoadCodexCredentials();
        if (creds is null)
            return ErrorData("codex", "Codex credentials not found. Run 'codex' once to log in (~/.codex/auth.json).");

        using var req = new HttpRequestMessage(HttpMethod.Get, CodexBaseUrl + CodexUsagePath);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {creds.Value.AccessToken}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrEmpty(creds.Value.AccountId))
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.Value.AccountId);

        var resp = await _httpClient.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return ErrorData("codex", "Codex token expired. Please run 'codex' to re-authenticate.");

        resp.EnsureSuccessStatusCode();

        var usage = await resp.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.CodexUsageResponse, ct)
            ?? throw new InvalidOperationException("Null response from Codex API");

        return BuildCodexUsageData(usage);
    }

    private (string AccessToken, string? AccountId, string? RefreshToken)? LoadCodexCredentials()
    {
        // Support CODEX_HOME env override
        var codexDir = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim() is { Length: > 0 } h
            ? h : Path.Combine(HomeDir, ".codex");

        var authPath = Path.Combine(codexDir, "auth.json");
        if (!File.Exists(authPath)) return null;

        try
        {
            var json = JsonDocument.Parse(File.ReadAllText(authPath));
            var root = json.RootElement;

            // Check for OPENAI_API_KEY shortcut
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKeyEl))
            {
                var key = apiKeyEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(key))
                    return (key, null, null);
            }

            // Standard tokens object
            if (root.TryGetProperty("tokens", out var tokens))
            {
                var at = tokens.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : null;
                var rt = tokens.TryGetProperty("refresh_token", out var rtEl) ? rtEl.GetString() : null;
                var ac = tokens.TryGetProperty("account_id", out var acEl) ? acEl.GetString() : null;
                if (!string.IsNullOrEmpty(at))
                    return (at!, ac, rt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Codex credentials");
        }

        return null;
    }

    private static UsageData BuildCodexUsageData(CodexUsageResponse usage)
    {
        UsageWindow? session = null, weekly = null, tertiary = null;
        CreditsInfo? credits = null;

        // Extract primary window
        if (usage.RateLimit?.PrimaryWindow is { } pw)
            session = SnapshotToWindow(pw);
        else if (usage.RateLimits?.Count > 0)
            session = SnapshotToWindow(usage.RateLimits[0]);
        else
            session = new UsageWindow { Used = (int)(usage.UsedPercent ?? usage.UsagePercent ?? 0), Limit = 100 };

        // Secondary
        if (usage.RateLimit?.SecondaryWindow is { } sw)
            weekly = SnapshotToWindow(sw);
        else if (usage.RateLimits?.Count > 1)
            weekly = SnapshotToWindow(usage.RateLimits[1]);

        // Tertiary (code review)
        if (usage.RateLimit?.CodeReviewWindow is { } crw)
            tertiary = SnapshotToWindow(crw);
        else if (usage.RateLimits?.Count > 2)
            tertiary = SnapshotToWindow(usage.RateLimits[2]);

        // Credits — Balance is returned as a JSON string (e.g. "0") so we parse it manually
        if (usage.Credits is { HasCredits: true, Unlimited: not true } c &&
            double.TryParse(c.Balance, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var balanceVal) &&
            balanceVal > 0)
            credits = new CreditsInfo { Used = 0, Limit = (decimal)balanceVal };

        var plan = usage.PlanType is { } pt ? MapCodexPlan(pt) : null;

        return new UsageData
        {
            Provider = "codex",
            Plan = plan,
            Session = session,
            Weekly = weekly,
            Tertiary = tertiary,
            Credits = credits,
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    private static UsageWindow SnapshotToWindow(CodexWindowSnapshot w)
    {
        var pct = w.UsedPercent != 0 ? w.UsedPercent : (w.UsagePercent ?? 0);
        DateTime? resetAt = w.ResetAt.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(w.ResetAt.Value).UtcDateTime
            : null;
        return new UsageWindow { Used = (int)pct, Limit = 100, ResetAt = resetAt };
    }

    private static string MapCodexPlan(string pt) => pt switch
    {
        "guest" => "Guest",
        "free" => "ChatGPT Free",
        "go" => "ChatGPT Go",
        "plus" => "ChatGPT Plus",
        "pro" => "ChatGPT Pro",
        "team" => "ChatGPT Team",
        "business" => "ChatGPT Business",
        "enterprise" => "ChatGPT Enterprise",
        "education" or "edu" => "ChatGPT Education",
        _ => $"ChatGPT {char.ToUpperInvariant(pt[0])}{pt[1..]}"
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  CLAUDE (Anthropic)
    // ═══════════════════════════════════════════════════════════════════════

    private const string ClaudeBaseUrl = "https://claude.ai/api";

    private async Task<UsageData> FetchClaudeAsync(CancellationToken ct)
    {
        var cookieHeader = GetBrowserCookieHeader(["claude.ai", "claude.com", "console.anthropic.com", "anthropic.com"]);
        if (string.IsNullOrEmpty(cookieHeader))
            return ErrorData("claude", "No Claude session cookie found in browser. Log in to claude.ai in Chrome or Edge.");

        var headers = BuildClaudeHeaders(cookieHeader);

        // Step 1: get org ID
        var orgId = await GetClaudeOrgIdAsync(headers, ct);
        if (orgId is null)
            return ErrorData("claude", "Failed to retrieve Claude organization. Token may be expired.");

        // Step 2: usage
        var usageResp = await GetClaudeUsageAsync(orgId, headers, ct);

        // Step 3: extra usage (credits) — optional
        ClaudeExtraUsageResponse? extra = null;
        try { extra = await GetClaudeExtraUsageAsync(orgId, headers, ct); }
        catch { /* optional */ }

        // Step 4: account info — optional
        ClaudeAccountResponse? account = null;
        try { account = await GetClaudeAccountAsync(headers, ct); }
        catch { /* optional */ }

        return BuildClaudeUsageData(usageResp, extra, account);
    }

    private Dictionary<string, string> BuildClaudeHeaders(string cookieHeader) => new()
    {
        ["Cookie"] = cookieHeader,
        ["Accept"] = "application/json",
        ["Origin"] = "https://claude.ai",
        ["Referer"] = "https://claude.ai/settings/usage",
        ["anthropic-client-platform"] = "web_claude_ai"
    };

    private async Task<string?> GetClaudeOrgIdAsync(Dictionary<string, string> headers, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{ClaudeBaseUrl}/organizations", headers);
        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var orgs = await resp.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.ListClaudeOrganization, ct);
        return orgs?.FirstOrDefault()?.Uuid;
    }

    private async Task<ClaudeUsageResponse> GetClaudeUsageAsync(string orgId, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{ClaudeBaseUrl}/organizations/{orgId}/usage", headers);
        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.ClaudeUsageResponse, ct)
               ?? new ClaudeUsageResponse();
    }

    private async Task<ClaudeExtraUsageResponse?> GetClaudeExtraUsageAsync(string orgId, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{ClaudeBaseUrl}/organizations/{orgId}/overage_spend_limit", headers);
        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.ClaudeExtraUsageResponse, ct);
    }

    private async Task<ClaudeAccountResponse?> GetClaudeAccountAsync(Dictionary<string, string> headers, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{ClaudeBaseUrl}/account", headers);
        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.ClaudeAccountResponse, ct);
    }

    private static UsageData BuildClaudeUsageData(
        ClaudeUsageResponse usage, ClaudeExtraUsageResponse? extra, ClaudeAccountResponse? account)
    {
        UsageWindow? session = null, weekly = null, tertiary = null;
        CreditsInfo? credits = null;

        // 5-hour primary window (utilization 0-1 or 0-100)
        if (usage.FiveHour is { } fh)
            session = ClaudeWindowToUsageWindow(fh, 5 * 60);

        // 7-day secondary
        if (usage.SevenDay is { } sd)
            weekly = ClaudeWindowToUsageWindow(sd, 7 * 24 * 60);

        // 7-day Opus / Sonnet tertiary
        if (usage.SevenDayOpus is { } opus)
            tertiary = ClaudeWindowToUsageWindow(opus, 7 * 24 * 60);
        else if (usage.SevenDaySonnet is { } sonnet)
            tertiary = ClaudeWindowToUsageWindow(sonnet, 7 * 24 * 60);

        // Credits
        if (extra is { IsEnabled: true, UsedCredits: not null })
        {
            var used = (decimal)(extra.UsedCredits.Value / 100.0);
            var limit = extra.MonthlyCreditLimit.HasValue ? (decimal)(extra.MonthlyCreditLimit.Value / 100.0) : 0m;
            credits = new CreditsInfo { Used = used, Limit = limit };
        }

        var plan = account?.RateLimitTier is { } tier ? MapClaudeTier(tier) : null;

        return new UsageData
        {
            Provider = "claude",
            Plan = plan,
            Session = session,
            Weekly = weekly,
            Tertiary = tertiary,
            Credits = credits,
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    private static UsageWindow ClaudeWindowToUsageWindow(ClaudeUsageWindow w, int windowMinutes)
    {
        var utilization = w.Utilization ?? 0;
        // Normalize: if <= 1.0 it's a fraction; otherwise already a percent
        var pct = utilization is > 0 and <= 1.0 ? utilization * 100 : utilization;

        DateTime? resetAt = null;
        if (w.ResetsAt is { } ra && DateTime.TryParse(ra, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            resetAt = dt.ToUniversalTime();

        return new UsageWindow
        {
            Used = (int)Math.Round(pct),
            Limit = 100,
            ResetAt = resetAt
        };
    }

    private static string MapClaudeTier(string tier) => tier.ToLowerInvariant() switch
    {
        "free" => "Claude Free",
        "pro" or "claude_pro" => "Claude Pro",
        "max" or "claude_max_5" or "claude_max_20" => "Claude Max",
        "team" => "Claude Team",
        "enterprise" => "Claude Enterprise",
        _ => $"Claude ({tier})"
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  GEMINI (Google)
    // ═══════════════════════════════════════════════════════════════════════

    private const string GeminiQuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";
    private const string GeminiTokenUrl = "https://oauth2.googleapis.com/token";

    private async Task<UsageData> FetchGeminiAsync(CancellationToken ct)
    {
        var creds = LoadGeminiCredentials();
        if (creds is null)
            return ErrorData("gemini", "Gemini credentials not found. Run 'gemini' once to log in (~/.gemini/oauth_creds.json).");

        // Refresh token if expired
        if (creds.IsExpired())
        {
            _logger.LogDebug("Gemini token expired, refreshing...");
            try { creds = await RefreshGeminiTokenAsync(creds, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh Gemini token");
                return ErrorData("gemini", "Gemini token expired and refresh failed. Run 'gemini' again.");
            }
        }

        if (string.IsNullOrEmpty(creds.AccessToken))
            return ErrorData("gemini", "Gemini access token is empty.");

        // Step 1: Discover Project ID (following CodexBar logic)
        var projectId = await DiscoverGeminiProjectAsync(creds.AccessToken, ct);

        // Step 2: Fetch Quota
        using var req = new HttpRequestMessage(HttpMethod.Post, GeminiQuotaUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {creds.AccessToken}");
        
        var body = projectId != null ? $"{{\"project\": \"{projectId}\"}}" : "{}";
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return ErrorData("gemini", "Gemini token invalid. Run 'gemini' again to re-authenticate.");
        resp.EnsureSuccessStatusCode();

        var quotaResp = await resp.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.GeminiQuotaResponse, ct)
            ?? new GeminiQuotaResponse();

        var email = ExtractEmailFromJwt(creds.IdToken);
        return BuildGeminiUsageData(quotaResp, email);
    }

    private async Task<string?> DiscoverGeminiProjectAsync(string accessToken, CancellationToken ct)
    {
        // 1. Try loadCodeAssist (most reliable for managed projects)
        var caProject = await LoadCodeAssistProjectAsync(accessToken, ct);
        if (caProject != null) return caProject;

        // 2. Try listing projects (for custom AI Studio/Vertex projects)
        return await ListAndFindGeminiProjectAsync(accessToken, ct);
    }

    private async Task<string?> LoadCodeAssistProjectAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Content = new StringContent("{\"metadata\":{\"ideType\":\"GEMINI_CLI\",\"pluginType\":\"GEMINI\"}}", Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            // cloudaicompanionProject can be a string or an object with 'id'/'projectId'
            if (root.TryGetProperty("cloudaicompanionProject", out var proj))
            {
                if (proj.ValueKind == JsonValueKind.String)
                {
                    var id = proj.GetString()?.Trim();
                    return string.IsNullOrEmpty(id) ? null : id;
                }
                if (proj.ValueKind == JsonValueKind.Object)
                {
                    if (proj.TryGetProperty("id", out var idEl)) return idEl.GetString()?.Trim();
                    if (proj.TryGetProperty("projectId", out var pidEl)) return pidEl.GetString()?.Trim();
                }
            }
        }
        catch { /* fallback */ }
        return null;
    }

    private async Task<string?> ListAndFindGeminiProjectAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://cloudresourcemanager.googleapis.com/v1/projects");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

            var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Array)
            {
                foreach (var proj in projects.EnumerateArray())
                {
                    var pid = proj.GetProperty("projectId").GetString();
                    if (pid == null) continue;

                    // Match logic from CodexBar: gen-lang-client prefix or generative-language label
                    if (pid.StartsWith("gen-lang-client", StringComparison.OrdinalIgnoreCase))
                        return pid;

                    if (proj.TryGetProperty("labels", out var labels) && labels.TryGetProperty("generative-language", out _))
                        return pid;
                }
            }
        }
        catch { /* fallback */ }
        return null;
    }

    private GeminiOAuthCredentials? LoadGeminiCredentials()
    {
        var credsPath = Path.Combine(HomeDir, ".gemini", "oauth_creds.json");
        if (!File.Exists(credsPath)) return null;
        try
        {
            var json = File.ReadAllText(credsPath);
            return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.GeminiOAuthCredentials);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Gemini credentials");
            return null;
        }
    }

    private async Task<GeminiOAuthCredentials> RefreshGeminiTokenAsync(GeminiOAuthCredentials creds, CancellationToken ct)
    {
        var clientCreds = LoadGeminiClientCredentials();
        if (clientCreds is null)
            throw new InvalidOperationException("Gemini OAuth client credentials not found.");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientCreds.Value.ClientId,
            ["client_secret"] = clientCreds.Value.ClientSecret,
            ["refresh_token"] = creds.RefreshToken ?? throw new InvalidOperationException("No refresh token"),
            ["grant_type"] = "refresh_token"
        });

        var resp = await _httpClient.PostAsync(GeminiTokenUrl, form, ct);
        resp.EnsureSuccessStatusCode();

        var refreshed = await resp.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.GeminiTokenRefreshResponse, ct)
            ?? throw new InvalidOperationException("Null token refresh response");

        // Update and save
        creds.AccessToken = refreshed.AccessToken;
        if (refreshed.IdToken is not null) creds.IdToken = refreshed.IdToken;
        if (refreshed.ExpiresIn.HasValue)
            creds.ExpiryDate = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + refreshed.ExpiresIn.Value) * 1000.0;

        SaveGeminiCredentials(creds);
        return creds;
    }

    private (string ClientId, string ClientSecret)? LoadGeminiClientCredentials()
    {
        var cfgPath = Path.Combine(HomeDir, ".gemini", "client_config.json");
        if (File.Exists(cfgPath))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                var r = doc.RootElement;
                if (r.TryGetProperty("client_id", out var cid) && r.TryGetProperty("client_secret", out var cs))
                    return (cid.GetString()!, cs.GetString()!);
            }
            catch { /* fallthrough */ }
        }

        // Env var fallback
        var id = Environment.GetEnvironmentVariable("GEMINI_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("GEMINI_CLIENT_SECRET");
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret))
            return (id, secret);

        return null;
    }

    private void SaveGeminiCredentials(GeminiOAuthCredentials creds)
    {
        try
        {
            var path = Path.Combine(HomeDir, ".gemini", "oauth_creds.json");
            File.WriteAllText(path, JsonSerializer.Serialize(creds, AppJsonSerializerContext.Default.GeminiOAuthCredentials));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save refreshed Gemini credentials");
        }
    }

    private static UsageData BuildGeminiUsageData(GeminiQuotaResponse quotaResp, string? email)
    {
        if (quotaResp.Buckets is not { Count: > 0 } buckets)
            return ErrorData("gemini", "No quota buckets in Gemini API response.");

        // Group by model, keep lowest remaining fraction per model
        var modelQuotas = new Dictionary<string, (double Fraction, string? ResetTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in buckets)
        {
            if (b.ModelId is null || b.RemainingFraction is null) continue;
            var key = b.ModelId;
            if (!modelQuotas.TryGetValue(key, out var current) || b.RemainingFraction.Value < current.Item1)
                modelQuotas[key] = (b.RemainingFraction.Value, b.ResetTime);
        }

        var flash = modelQuotas.Where(kv => kv.Key.Contains("flash", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Item1).Cast<KeyValuePair<string, (double, string?)>?>().FirstOrDefault();
        var pro = modelQuotas.Where(kv => kv.Key.Contains("pro", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Item1).Cast<KeyValuePair<string, (double, string?)>?>().FirstOrDefault();

        // Primary = Pro if available, else Flash
        var primary = pro ?? flash
            ?? modelQuotas.Cast<KeyValuePair<string, (double, string?)>?>().FirstOrDefault();

        UsageWindow? session = null, tertiary = null;

        if (primary.HasValue)
        {
            var pct = (1.0 - primary.Value.Value.Item1) * 100.0;
            session = new UsageWindow
            {
                Used = (int)Math.Round(pct),
                Limit = 100,
                ResetAt = ParseIso(primary.Value.Value.Item2)
            };
        }

        // Secondary model (Flash) if Pro is primary
        if (pro.HasValue && flash.HasValue)
        {
            var pct = (1.0 - flash.Value.Value.Item1) * 100.0;
            tertiary = new UsageWindow
            {
                Used = (int)Math.Round(pct),
                Limit = 100,
                ResetAt = ParseIso(flash.Value.Value.Item2)
            };
        }

        return new UsageData
        {
            Provider = "gemini",
            Plan = email is not null ? $"Gemini ({email})" : "Gemini",
            Session = session,
            Tertiary = tertiary,
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ANTIGRAVITY (CLI)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<UsageData> FetchAntigravityAsync(CancellationToken ct)
    {
        try
        {
            // Antigravity is a local provider. We talk directly to the Antigravity language server.
            // We use PowerShell to discover the process, its CSRF token, and its listening port.
            var script = @"
$procs = Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'language_server_windows_x64' };
if (-not $procs) { exit 1 };
foreach ($p in $procs) {
    $csrf = $null;
    if ($p.CommandLine -match '--csrf_token\s+([a-zA-Z0-9-]+)') { $csrf = $Matches[1] };
    if ($csrf) {
        $targetPid = $p.ProcessId;
        $conns = Get-NetTCPConnection | Where-Object { $_.OwningProcess -eq $targetPid -and $_.State -eq 'Listen' };
        foreach ($c in $conns) {
            $port = $c.LocalPort;
            try {
                $url = 'http://127.0.0.1:' + $port + '/exa.language_server_pb.LanguageServerService/GetUserStatus';
                $bodyObj = @{ ideName = 'antigravity'; extensionName = 'antigravity'; locale = 'en'; ideVersion = 'unknown' };
                $body = $bodyObj | ConvertTo-Json -Compress;
                $headers = @{ 'X-Codeium-Csrf-Token' = $csrf; 'Connect-Protocol-Version' = '1' };
                $resp = Invoke-RestMethod -Uri $url -Method Post -Body $body -Headers $headers -ContentType 'application/json' -TimeoutSec 2 -ErrorAction Stop;
                if ($resp.userStatus) {
                    $us = $resp.userStatus;
                    $plan = if ($us.planStatus.planInfo.planName) { $us.planStatus.planInfo.planName } else { 'Antigravity' };
                    $configs = $us.cascadeModelConfigData.clientModelConfigs;
                    $claude = $configs | Where-Object { $_.label -match 'Claude' -and $_.label -notmatch 'Thinking' } | Select-Object -First 1;
                    $geminiPro = $configs | Where-Object { $_.label -match 'Pro' -and ($_.label -match 'Low' -or $_.label -match '1\.5') } | Select-Object -First 1;
                    if (-not $geminiPro) { $geminiPro = $configs | Where-Object { $_.label -match 'Pro' } | Select-Object -First 1 };
                    $geminiFlash = $configs | Where-Object { $_.label -match 'Flash' } | Select-Object -First 1;
                    $primary = if ($claude) { $claude } else { $configs | Sort-Object { $_.quotaInfo.remainingFraction } | Select-Object -First 1 };
                    $out = @{
                        provider = 'antigravity';
                        source = 'local';
                        usage = @{
                            loginMethod = $plan;
                            primary = if ($primary) { $rem = $primary.quotaInfo.remainingFraction; if ($null -eq $rem) { $rem = 1 }; @{ label = $primary.label; usedPercent = [math]::Round((1 - $rem) * 100); resetsAt = $primary.quotaInfo.resetTime } };
                            secondary = if ($geminiPro) { $rem = $geminiPro.quotaInfo.remainingFraction; if ($null -eq $rem) { $rem = 1 }; @{ label = $geminiPro.label; usedPercent = [math]::Round((1 - $rem) * 100); resetsAt = $geminiPro.quotaInfo.resetTime } };
                            tertiary = if ($geminiFlash) { $rem = $geminiFlash.quotaInfo.remainingFraction; if ($null -eq $rem) { $rem = 1 }; @{ label = $geminiFlash.label; usedPercent = [math]::Round((1 - $rem) * 100); resetsAt = $geminiFlash.quotaInfo.resetTime } };
                        };
                    };
                    $out | ConvertTo-Json -Depth 10;
                    exit 0;
                }
            } catch { }
        }
    }
}
exit 1;
";
            var result = await RunPowerShellCommandAsync(script.Replace("\r\n", " ").Replace("\n", " "), ct);
            if (!result.Success)
            {
                var isRunning = (await RunPowerShellCommandAsync("Get-Process -Name *language_server* -ErrorAction SilentlyContinue", ct)).Success;
                var errMsg = isRunning
                    ? "Antigravity language server is running but could not be probed. Check if it's logged in."
                    : "Antigravity is not running. Launch Antigravity editor to see usage.";
                return _cacheService.Get("antigravity") ?? ErrorData("antigravity", errMsg);
            }

            if (string.IsNullOrWhiteSpace(result.Output))
            {
                return _cacheService.Get("antigravity")
                    ?? ErrorData("antigravity", "Could not parse antigravity probe output.");
            }

            var parsed = TryParseUsageDataJson(result.Output, "antigravity");
            if (parsed != null)
                return parsed;

            return _cacheService.Get("antigravity")
                ?? ErrorData("antigravity", "Could not parse antigravity probe output.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch antigravity via local probe");
            return _cacheService.Get("antigravity")
                ?? ErrorData("antigravity", $"Antigravity probe error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CURSOR
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<UsageData> FetchCursorAsync(CancellationToken ct)
    {
        var cookieHeader = GetBrowserCookieHeader(["cursor.com", "cursor.sh"]);
        if (string.IsNullOrEmpty(cookieHeader))
            return ErrorData("cursor", "No Cursor session cookie found in browser. Log in to cursor.com in Chrome or Edge.");

        // Fetch usage summary and user info in parallel
        var usageTask = FetchCursorUsageSummaryAsync(cookieHeader, ct);
        var userTask = FetchCursorUserInfoAsync(cookieHeader, ct);
        await Task.WhenAll(usageTask, userTask);

        var summary = usageTask.Result;
        var userInfo = userTask.IsCompletedSuccessfully ? userTask.Result : null;

        if (summary is null)
            return ErrorData("cursor", "Failed to fetch Cursor usage. Token may be expired.");

        return BuildCursorUsageData(summary, userInfo);
    }

    private async Task<CursorUsageSummary?> FetchCursorUsageSummaryAsync(string cookieHeader, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.CursorUsageSummary, ct);
        }
        catch { return null; }
    }

    private async Task<CursorUserInfo?> FetchCursorUserInfoAsync(string cookieHeader, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/auth/me");
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.CursorUserInfo, ct);
        }
        catch { return null; }
    }

    private static UsageData BuildCursorUsageData(CursorUsageSummary summary, CursorUserInfo? userInfo)
    {
        UsageWindow? session = null, weekly = null, tertiary = null;
        CreditsInfo? credits = null;

        var billingEnd = ParseIso(summary.BillingCycleEnd);

        if (summary.IndividualUsage?.Plan is { } plan)
        {
            var usedCents = (double)(plan.Used ?? 0);
            var limitCents = (double)(plan.Breakdown?.Total ?? plan.Limit ?? 0);
            var pct = limitCents > 0
                ? usedCents / limitCents * 100.0
                : (plan.TotalPercentUsed ?? 0) * 100.0;

            session = new UsageWindow
            {
                Used = (int)Math.Round(pct),
                Limit = 100,
                ResetAt = billingEnd
            };

            if (plan.AutoPercentUsed.HasValue)
                weekly = new UsageWindow
                {
                    Used = (int)Math.Round(plan.AutoPercentUsed.Value * 100.0),
                    Limit = 100,
                    ResetAt = billingEnd
                };

            if (plan.ApiPercentUsed.HasValue)
                tertiary = new UsageWindow
                {
                    Used = (int)Math.Round(plan.ApiPercentUsed.Value * 100.0),
                    Limit = 100,
                    ResetAt = billingEnd
                };

            if (limitCents > 0)
                credits = new CreditsInfo
                {
                    Used = (decimal)(usedCents / 100.0),
                    Limit = (decimal)(limitCents / 100.0)
                };
        }

        var plan2 = summary.MembershipType is { } mt ? MapCursorPlan(mt) : null;

        return new UsageData
        {
            Provider = "cursor",
            Plan = plan2,
            Session = session,
            Weekly = weekly,
            Tertiary = tertiary,
            Credits = credits,
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    private static string MapCursorPlan(string mt) => mt.ToLowerInvariant() switch
    {
        "enterprise" => "Cursor Enterprise",
        "pro" => "Cursor Pro",
        "hobby" => "Cursor Hobby",
        "team" => "Cursor Team",
        _ => $"Cursor {char.ToUpperInvariant(mt[0])}{mt[1..]}"
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  Windows Cookie Reader (Chrome / Edge) — supports AES-256-GCM (v10/v11)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Browser profile DB paths and their matching Local State key files.</summary>
    private static readonly (string DbPath, string LocalStatePath)[] BrowserProfiles =
    [
        (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data\Default\Network\Cookies"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data\Local State")
        ),
        (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data\Default\Cookies"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data\Local State")
        ),
        (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data\Default\Network\Cookies"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data\Local State")
        ),
        (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data\Default\Cookies"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data\Local State")
        ),
    ];

    // Cache of decrypted browser master keys keyed by Local State path
    private readonly Dictionary<string, byte[]> _masterKeyCache = new();

    /// <summary>
    /// Reads cookies for the given host domains from Chrome/Edge SQLite stores,
    /// decrypts them (AES-GCM or DPAPI), and returns a Cookie header string.
    /// </summary>
    private string GetBrowserCookieHeader(string[] domains)
    {
        foreach (var domain in domains)
        {
            var header = ReadCookieHeaderForDomain(domain);
            if (!string.IsNullOrEmpty(header))
                return header;
        }
        return string.Empty;
    }

    private string ReadCookieHeaderForDomain(string domain)
    {
        foreach (var (dbPath, localStatePath) in BrowserProfiles)
        {
            if (!File.Exists(dbPath)) continue;
            try
            {
                var masterKey = GetOrLoadMasterKey(localStatePath);
                var cookies = ReadCookiesFromDb(dbPath, domain, masterKey);
                if (cookies.Count > 0)
                    return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read cookies from {Db} for {Domain}", dbPath, domain);
            }
        }
        return string.Empty;
    }

    private record CookieEntry(string Name, string Value);

    /// <summary>
    /// Loads and DPAPI-decrypts the AES master key from Chrome/Edge Local State.
    /// Returns null if the file is missing or the key can't be decrypted.
    /// </summary>
    private byte[]? GetOrLoadMasterKey(string localStatePath)
    {
        if (_masterKeyCache.TryGetValue(localStatePath, out var cached))
            return cached;

        if (!File.Exists(localStatePath)) return null;

        try
        {
            var json = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!json.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var keyEl)) return null;

            var encryptedKeyB64 = keyEl.GetString();
            if (string.IsNullOrEmpty(encryptedKeyB64)) return null;

            var encryptedKey = Convert.FromBase64String(encryptedKeyB64);

            // Strip the "DPAPI" prefix (first 5 bytes)
            if (encryptedKey.Length < 5) return null;
            var dpapiBlobBytes = encryptedKey.Skip(5).ToArray();

            var masterKey = ProtectedData.Unprotect(dpapiBlobBytes, null, DataProtectionScope.CurrentUser);
            _masterKeyCache[localStatePath] = masterKey;
            return masterKey;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load browser master key from {Path}", localStatePath);
            return null;
        }
    }

    private List<CookieEntry> ReadCookiesFromDb(string dbPath, string domain, byte[]? masterKey)
    {
        // Copy the DB to temp since Chrome may have it locked
        var tmp = Path.Combine(Path.GetTempPath(), $"codexbar_cookies_{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(dbPath, tmp, overwrite: true);

            var results = new List<CookieEntry>();
            using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name, encrypted_value FROM cookies " +
                "WHERE host_key = @d OR host_key = @ds";
            cmd.Parameters.AddWithValue("@d", domain);
            cmd.Parameters.AddWithValue("@ds", "." + domain);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var encryptedBytes = reader.GetFieldValue<byte[]>(1);
                var decrypted = DecryptCookieValue(encryptedBytes, masterKey);
                if (!string.IsNullOrEmpty(decrypted))
                    results.Add(new CookieEntry(name, decrypted));
            }

            return results;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private string? DecryptCookieValue(byte[] encrypted, byte[]? masterKey)
    {
        if (encrypted.Length == 0) return null;

        try
        {
            // Modern Chrome 80+ / Edge: v10 or v11 prefix → AES-256-GCM
            if (encrypted.Length > 3 && encrypted[0] == (byte)'v' &&
                (encrypted[1] == (byte)'1') && masterKey != null)
            {
                return TryAesGcmDecrypt(encrypted, masterKey);
            }

            // Older format: pure DPAPI
            return TryDpapiDecrypt(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cookie decryption failed");
            return null;
        }
    }

    /// <summary>
    /// Decrypts a Chrome v10/v11 cookie using AES-256-GCM.
    /// Layout after the 3-byte version prefix:
    ///   [12 bytes IV] [ciphertext + 16-byte GCM tag]
    /// </summary>
    private static string? TryAesGcmDecrypt(byte[] encrypted, byte[] key)
    {
        try
        {
            // Skip 3-byte version prefix ("v10" or "v11")
            const int prefixLen = 3;
            const int ivLen = 12;
            const int tagLen = 16;

            if (encrypted.Length < prefixLen + ivLen + tagLen) return null;

            var iv = encrypted.AsSpan(prefixLen, ivLen).ToArray();
            var cipherWithTag = encrypted.AsSpan(prefixLen + ivLen).ToArray();

            if (cipherWithTag.Length < tagLen) return null;

            var cipherLen = cipherWithTag.Length - tagLen;
            var cipher = cipherWithTag.AsSpan(0, cipherLen).ToArray();
            var tag = cipherWithTag.AsSpan(cipherLen, tagLen).ToArray();

            var plaintext = new byte[cipherLen];

            using var aes = new System.Security.Cryptography.AesGcm(key, tagLen);
            aes.Decrypt(iv, cipher, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryDpapiDecrypt(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Utilities
    // ═══════════════════════════════════════════════════════════════════════

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, Dictionary<string, string> headers)
    {
        var req = new HttpRequestMessage(method, url);
        foreach (var (k, v) in headers)
            req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }

    private static string? GetEnvironmentTrimmed(string key)
    {
        var value = Environment.GetEnvironmentVariable(key)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim('"', '\'');
    }

    private static string? GetProviderApiKey(string providerId, string[] envKeys)
    {
        var configValue = GetProviderApiKeyFromConfig(providerId);
        if (!string.IsNullOrWhiteSpace(configValue))
            return configValue;

        foreach (var key in envKeys)
        {
            var value = GetEnvironmentTrimmed(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? GetProviderApiKeyFromConfig(string providerId)
    {
        var path = Path.Combine(HomeDir, ".codexbar", "config.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("providerConfig", out var providerConfig) &&
                providerConfig.ValueKind == JsonValueKind.Object &&
                providerConfig.TryGetProperty(providerId, out var providerNode) &&
                providerNode.ValueKind == JsonValueKind.Object &&
                providerNode.TryGetProperty("apiKey", out var apiKeyNode))
            {
                var value = apiKeyNode.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            if (root.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Array)
            {
                foreach (var provider in providers.EnumerateArray())
                {
                    var id = provider.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                    if (!string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (provider.TryGetProperty("apiKey", out var providerApiKeyNode))
                    {
                        var value = providerApiKeyNode.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string GetZaiQuotaUrl()
    {
        var full = GetEnvironmentTrimmed("Z_AI_QUOTA_URL");
        if (!string.IsNullOrWhiteSpace(full))
            return full!;

        var host = GetEnvironmentTrimmed("Z_AI_API_HOST");
        if (!string.IsNullOrWhiteSpace(host))
            return $"https://{host!.TrimEnd('/')}/api/monitor/usage/quota/limit";

        return "https://api.z.ai/api/monitor/usage/quota/limit";
    }

    private static string? FirstNonEmptyString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private static int? GetWindowMinutes(JsonElement limit)
    {
        if (!limit.TryGetProperty("number", out var numberEl) || !numberEl.TryGetInt32(out var number) || number <= 0)
            return null;
        if (!limit.TryGetProperty("unit", out var unitEl) || !unitEl.TryGetInt32(out var unit))
            return null;

        return unit switch
        {
            5 => number,
            3 => number * 60,
            1 => number * 24 * 60,
            6 => number * 7 * 24 * 60,
            _ => null
        };
    }

    private static UsageWindow JsonLimitToUsageWindow(JsonElement limit)
    {
        var percent = limit.TryGetProperty("percentage", out var pctEl) && pctEl.TryGetDouble(out var pct)
            ? pct
            : 0;
        var resetAt = limit.TryGetProperty("nextResetTime", out var resetEl) && resetEl.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : (DateTime?)null;
        return new UsageWindow
        {
            Used = (int)Math.Round(percent),
            Limit = 100,
            ResetAt = resetAt
        };
    }

    private static double? FindDouble(JsonElement root, params string[][] paths)
    {
        foreach (var path in paths)
        {
            if (TryGetPath(root, path, out var value) && TryGetDouble(value, out var number))
                return number;
        }
        return null;
    }

    private static bool TryGetPath(JsonElement root, string[] path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out value))
            return true;

        value = 0;
        return false;
    }

    private static double? TryGetHeaderDouble(HttpResponseMessage response, string headerName)
    {
        if (!response.Headers.TryGetValues(headerName, out var values))
            return null;
        var raw = values.FirstOrDefault();
        return double.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static UsageWindow? BuildPercentWindow(double used, double limit, DateTime? resetAt)
    {
        if (limit <= 0) return null;
        return new UsageWindow
        {
            Used = (int)Math.Round(used),
            Limit = (int)Math.Round(limit),
            ResetAt = resetAt
        };
    }

    private static void AddOpenRouterHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var referer = GetEnvironmentTrimmed("OPENROUTER_HTTP_REFERER");
        if (!string.IsNullOrWhiteSpace(referer))
            request.Headers.TryAddWithoutValidation("HTTP-Referer", referer);
        request.Headers.TryAddWithoutValidation("X-Title", GetEnvironmentTrimmed("OPENROUTER_X_TITLE") ?? "CodexBar");
    }

    private static (int Granted, int Remaining, DateTime? Expiration) ParseWarpBonusCredits(JsonElement user)
    {
        var granted = 0;
        var remaining = 0;
        DateTime? earliestExpiry = null;

        void Accumulate(JsonElement grants)
        {
            if (grants.ValueKind != JsonValueKind.Array) return;
            foreach (var grant in grants.EnumerateArray())
            {
                var g = grant.TryGetProperty("requestCreditsGranted", out var grantedEl) ? grantedEl.GetInt32() : 0;
                var r = grant.TryGetProperty("requestCreditsRemaining", out var remainingEl) ? remainingEl.GetInt32() : 0;
                granted += g;
                remaining += r;
                if (r > 0 && grant.TryGetProperty("expiration", out var expirationEl))
                {
                    var expiration = ParseIso(expirationEl.GetString());
                    if (expiration.HasValue && (!earliestExpiry.HasValue || expiration < earliestExpiry))
                        earliestExpiry = expiration;
                }
            }
        }

        if (user.TryGetProperty("bonusGrants", out var bonusGrants))
            Accumulate(bonusGrants);

        if (user.TryGetProperty("workspaces", out var workspaces) && workspaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var workspace in workspaces.EnumerateArray())
            {
                if (workspace.TryGetProperty("bonusGrantsInfo", out var info) && info.TryGetProperty("grants", out var grants))
                    Accumulate(grants);
            }
        }

        return (granted, remaining, earliestExpiry);
    }

    private static UsageWindow? CopilotQuotaToUsageWindow(JsonElement quota)
    {
        if (quota.ValueKind != JsonValueKind.Object)
            return null;
        if (quota.TryGetProperty("isPlaceholder", out var placeholderEl) && placeholderEl.GetBoolean())
            return null;

        double? percentRemaining = null;
        if (quota.TryGetProperty("percentRemaining", out var percentEl) && TryGetDouble(percentEl, out var percentValue))
            percentRemaining = percentValue;
        else if (quota.TryGetProperty("percent_remaining", out var percentSnakeEl) && TryGetDouble(percentSnakeEl, out percentValue))
            percentRemaining = percentValue;

        if (!percentRemaining.HasValue)
            return null;

        return new UsageWindow
        {
            Used = (int)Math.Round(Math.Max(0, 100 - percentRemaining.Value)),
            Limit = 100
        };
    }

    private static string? FindJetBrainsQuotaFile()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Google")
        };

        return candidates
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "AIAssistantQuotaManager2.xml", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? ExtractXmlOptionValue(string xml, string optionName)
    {
        var pattern = $"<option[^>]*name=\"{Regex.Escape(optionName)}\"[^>]*value=\"([^\"]*)\"";
        return RegexCapture(xml, pattern);
    }

    private static string StripAnsi(string value)
    {
        return Regex.Replace(value, @"\x1B\[[0-9;?]*[ -/]*[@-~]", string.Empty);
    }

    private async Task<UsageData> FetchOpenCodeFamilyAsync(string providerId, string baseUrl, string serverUrl, bool subscriptionApi, CancellationToken ct)
    {
        var cookieHeader = GetBrowserCookieHeader(["opencode.ai"]);
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return ErrorData(providerId, "No OpenCode session cookie found. Log in to opencode.ai in Chrome or Edge.");

        var workspaceOverride = GetEnvironmentTrimmed("CODEXBAR_OPENCODE_WORKSPACE_ID");
        var workspaceId = NormalizeWorkspaceId(workspaceOverride) ?? await FetchOpenCodeWorkspaceIdAsync(baseUrl, serverUrl, cookieHeader, ct);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return ErrorData(providerId, "OpenCode workspace id could not be resolved.");

        var text = subscriptionApi
            ? await FetchOpenCodeSubscriptionTextAsync(serverUrl, cookieHeader, workspaceId!, ct)
            : await FetchOpenCodeGoPageAsync(baseUrl, cookieHeader, workspaceId!, ct);

        if (string.IsNullOrWhiteSpace(text))
            return ErrorData(providerId, "OpenCode usage payload was empty.");

        var rollingPercent = ExtractFirstDouble(text,
            @"rollingUsage[^}]*?usagePercent\s*:\s*([0-9]+(?:\.[0-9]+)?)",
            "\"rollingUsage\"[\\s\\S]*?\"usagePercent\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)");
        var rollingReset = ExtractFirstInt(text,
            @"rollingUsage[^}]*?resetInSec\s*:\s*([0-9]+)",
            "\"rollingUsage\"[\\s\\S]*?\"resetInSec\"\\s*:\\s*([0-9]+)");
        var weeklyPercent = ExtractFirstDouble(text,
            @"weeklyUsage[^}]*?usagePercent\s*:\s*([0-9]+(?:\.[0-9]+)?)",
            "\"weeklyUsage\"[\\s\\S]*?\"usagePercent\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)");
        var weeklyReset = ExtractFirstInt(text,
            @"weeklyUsage[^}]*?resetInSec\s*:\s*([0-9]+)",
            "\"weeklyUsage\"[\\s\\S]*?\"resetInSec\"\\s*:\\s*([0-9]+)");

        if (!rollingPercent.HasValue && !weeklyPercent.HasValue)
            return ErrorData(providerId, text.Contains("sign in", StringComparison.OrdinalIgnoreCase) ? "OpenCode session cookie expired." : "Could not parse OpenCode usage payload.");

        return new UsageData
        {
            Provider = providerId,
            Plan = providerId.Equals("opencodego", StringComparison.OrdinalIgnoreCase) ? "OpenCode Go" : "OpenCode",
            Session = rollingPercent.HasValue ? new UsageWindow { Used = (int)Math.Round(rollingPercent.Value), Limit = 100, ResetAt = rollingReset.HasValue ? DateTime.UtcNow.AddSeconds(rollingReset.Value) : null } : null,
            Weekly = weeklyPercent.HasValue ? new UsageWindow { Used = (int)Math.Round(weeklyPercent.Value), Limit = 100, ResetAt = weeklyReset.HasValue ? DateTime.UtcNow.AddSeconds(weeklyReset.Value) : null } : null,
            Status = "web",
            FetchedAt = DateTime.UtcNow
        };
    }

    private async Task<string?> FetchOpenCodeWorkspaceIdAsync(string baseUrl, string serverUrl, string cookieHeader, CancellationToken ct)
    {
        var getText = await FetchOpenCodeServerTextAsync(serverUrl, cookieHeader, null, "GET", new Uri(baseUrl), ct);
        var id = NormalizeWorkspaceId(RegexCapture(getText ?? string.Empty, "id\\s*:\\s*\"(wrk_[^\"]+)\"")
            ?? RegexCapture(getText ?? string.Empty, "\"(wrk_[A-Za-z0-9]+)\""));
        if (!string.IsNullOrWhiteSpace(id)) return id;

        var postText = await FetchOpenCodeServerTextAsync(serverUrl, cookieHeader, "[]", "POST", new Uri(baseUrl), ct);
        return NormalizeWorkspaceId(RegexCapture(postText ?? string.Empty, "id\\s*:\\s*\"(wrk_[^\"]+)\"")
            ?? RegexCapture(postText ?? string.Empty, "\"(wrk_[A-Za-z0-9]+)\""));
    }

    private async Task<string?> FetchOpenCodeSubscriptionTextAsync(string serverUrl, string cookieHeader, string workspaceId, CancellationToken ct)
    {
        var referer = new Uri($"https://opencode.ai/workspace/{workspaceId}/billing");
        return await FetchOpenCodeServerTextAsync(serverUrl, cookieHeader, $"[\"{workspaceId}\"]", "GET", referer, ct)
            ?? await FetchOpenCodeServerTextAsync(serverUrl, cookieHeader, $"[\"{workspaceId}\"]", "POST", referer, ct);
    }

    private async Task<string?> FetchOpenCodeGoPageAsync(string baseUrl, string cookieHeader, string workspaceId, CancellationToken ct)
    {
        return await FetchHtmlWithCookieHeaderAsync($"{baseUrl}/workspace/{workspaceId}/go", cookieHeader, ct);
    }

    private async Task<string?> FetchOpenCodeServerTextAsync(string serverUrl, string cookieHeader, string? argsJson, string method, Uri referer, CancellationToken ct)
    {
        var url = $"{serverUrl}";
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        req.Headers.TryAddWithoutValidation("Accept", "text/javascript, application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Origin", "https://opencode.ai");
        req.Headers.TryAddWithoutValidation("Referer", referer.ToString());
        req.Headers.TryAddWithoutValidation("x-server-fn-method", method);
        req.Headers.TryAddWithoutValidation("x-server-fn-id", argsJson == null ? "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f" : "7abeebee372f304e050aaaf92be863f4a86490e382f8c79db68fd94040d691b4");
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            req.Content = new StringContent(argsJson ?? "[]", Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string? NormalizeWorkspaceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("wrk_", StringComparison.OrdinalIgnoreCase)) return trimmed;
        var match = RegexCapture(trimmed, @"(wrk_[A-Za-z0-9]+)");
        return match;
    }

    private static UsageWindow BuildWindowFromStringUsageDetail(JsonElement detail)
    {
        var used = detail.TryGetProperty("used", out var usedEl) && int.TryParse(usedEl.GetString(), out var usedVal) ? usedVal : 0;
        var limit = detail.TryGetProperty("limit", out var limitEl) && int.TryParse(limitEl.GetString(), out var limitVal) ? limitVal : 0;
        var resetAt = detail.TryGetProperty("resetTime", out var resetEl) ? ParseIso(resetEl.GetString()) : null;
        return new UsageWindow { Used = used, Limit = Math.Max(limit, 1), ResetAt = resetAt };
    }

    private async Task<string?> FetchHtmlWithProviderCookiesAsync(string url, string[] domains, CancellationToken ct)
    {
        var cookieHeader = GetBrowserCookieHeader(domains);
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return null;
        return await FetchHtmlWithCookieHeaderAsync(url, cookieHeader, ct);
    }

    private async Task<string?> FetchHtmlWithCookieHeaderAsync(string url, string cookieHeader, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Referer", url);
        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string? ExtractCookieValue(string cookieHeader, string cookieName)
    {
        var exact = RegexCapture(cookieHeader, $@"(?:^|;\s*){Regex.Escape(cookieName)}=([^;]+)");
        if (!string.IsNullOrWhiteSpace(exact)) return exact;

        var chunkPattern = $@"(?:^|;\s*){Regex.Escape(cookieName)}\.(\d+)=([^;]+)";
        var matches = Regex.Matches(cookieHeader, chunkPattern);
        if (matches.Count == 0) return null;
        return string.Concat(matches.Cast<Match>().OrderBy(m => int.Parse(m.Groups[1].Value)).Select(m => m.Groups[2].Value));
    }

    private static string? ExtractJsObject(string html, string token)
    {
        var tokenIndex = html.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (tokenIndex < 0) return null;
        var braceIndex = html.IndexOf('{', tokenIndex);
        if (braceIndex < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = braceIndex; i < html.Length; i++)
        {
            var ch = html[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return html.Substring(braceIndex, i - braceIndex + 1);
            }
        }

        return null;
    }

    private static double? ParseUsageBlockPercent(string html, string label)
    {
        var segment = ExtractUsageSegment(html, label);
        return segment == null ? null : RegexCaptureDouble(segment, @"([0-9]+(?:\.[0-9]+)?)\s*%\s*used") ?? RegexCaptureDouble(segment, @"width:\s*([0-9]+(?:\.[0-9]+)?)%");
    }

    private static DateTime? ParseUsageBlockDate(string html, string label)
    {
        var segment = ExtractUsageSegment(html, label);
        return segment == null ? null : ParseIso(RegexCapture(segment, "data-time=\"([^\"]+)\""));
    }

    private static string? ExtractUsageSegment(string html, string label)
    {
        var idx = html.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var length = Math.Min(800, html.Length - idx);
        return html.Substring(idx, length);
    }

    private static string? RegexCapture(string text, string pattern, int groupIndex = 1)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success && match.Groups.Count > groupIndex ? match.Groups[groupIndex].Value : null;
    }

    private static double? RegexCaptureDouble(string text, string pattern, int groupIndex = 1)
    {
        var raw = RegexCapture(text, pattern, groupIndex);
        return double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? RegexCaptureInt(string text, string pattern, int groupIndex = 1)
    {
        var raw = RegexCapture(text, pattern, groupIndex);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static double? ExtractFirstDouble(string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var value = RegexCaptureDouble(text, pattern);
            if (value.HasValue) return value;
        }
        return null;
    }

    private static int? ExtractFirstInt(string text, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var value = RegexCaptureInt(text, pattern);
            if (value.HasValue) return value;
        }
        return null;
    }

    private static DateTime? ParseMonthDayReset(string text)
    {
        var raw = RegexCapture(text, @"resets on\s+([0-9]{2}/[0-9]{2})");
        if (raw == null) return null;
        var parts = raw.Split('/');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day)) return null;
        var year = DateTime.UtcNow.Year;
        var candidate = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        if (candidate < DateTime.UtcNow.AddDays(-1)) candidate = candidate.AddYears(1);
        return candidate;
    }

    private static DateTime? ParseIso(string? s)
    {
        if (s is null) return null;
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return null;
    }

    private static string? ExtractEmailFromJwt(string? token)
    {
        if (token is null) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1];
            // Pad to multiple of 4
            payload = payload.Replace('-', '+').Replace('_', '/');
            var rem = payload.Length % 4;
            if (rem != 0) payload += new string('=', 4 - rem);

            var decoded = Convert.FromBase64String(payload);
            var doc = JsonDocument.Parse(decoded);
            return doc.RootElement.TryGetProperty("email", out var el) ? el.GetString() : null;
        }
        catch { return null; }
    }

    private string? GetProviderAuthSecret(string providerId, string[] envKeys)
    {
        // 1. Try settings.json (user configured via UI)
        var config = _settingsService.Settings.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(config?.AuthSecret))
            return config.AuthSecret;

        // 2. Try environment variables
        foreach (var key in envKeys)
        {
            var value = GetEnvironmentTrimmed(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private async Task<UsageData> FetchXingchenAsync(CancellationToken ct)
    {
        var cookie = GetProviderAuthSecret("xingchen", ["XINGCHEN_COOKIE", "MAAS_COOKIE"]);
        if (string.IsNullOrWhiteSpace(cookie))
            return ErrorData("xingchen", "Cookie not found. Please log in to maas.xfyun.cn and configure the cookie in Settings.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://maas.xfyun.cn/api/v1/gpt-finetune/coding-plan/list?page=1&size=6");
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Referer", "https://maas.xfyun.cn/customer/coding-plan");

            var resp = await _httpClient.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return ErrorData("xingchen", "Cookie invalid or expired. Please re-configure in Settings.");
            
            resp.EnsureSuccessStatusCode();

            var xingchenResp = await resp.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.XingchenUsageResponse, ct);
            if (xingchenResp == null || xingchenResp.Code != 0 || xingchenResp.Data?.Rows?.Count == 0)
                return ErrorData("xingchen", "Failed to retrieve usage data from Xingchen API.");

            var row = xingchenResp.Data!.Rows![0];
            var usage = row.CodingPlanUsageDTO;
            if (usage == null)
                return ErrorData("xingchen", "Xingchen API response missing usage data.");
            
            DateTime? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(row.ExpiresAt) && DateTime.TryParse(row.ExpiresAt, out var dt))
                expiresAt = dt.ToUniversalTime();

            return new UsageData
            {
                Provider = "xingchen",
                Plan = "Xingchen Coding",
                Session = usage.Rp5hLimit > 0 ? new UsageWindow 
                { 
                    Label = "5小时",
                    Used = (int)usage.Rp5hUsage, 
                    Limit = (int)usage.Rp5hLimit,
                    ResetAt = expiresAt
                } : null,
                Weekly = usage.RpwLimit > 0 ? new UsageWindow
                {
                    Label = "每周",
                    Used = (int)usage.RpwUsage,
                    Limit = (int)usage.RpwLimit,
                    ResetAt = expiresAt
                } : null,
                Tertiary = usage.PackageLimit > 0 ? new UsageWindow
                {
                    Label = "月度总量",
                    Used = (int)usage.PackageUsage,
                    Limit = (int)usage.PackageLimit,
                    ResetAt = expiresAt
                } : null,
                Credits = usage.PackageLimit > 0 ? new CreditsInfo
                {
                    Used = (decimal)usage.PackageUsage,
                    Limit = (decimal)usage.PackageLimit
                } : null,
                Status = "api",
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Xingchen usage");
            return ErrorData("xingchen", $"Xingchen API error: {ex.Message}");
        }
    }
}
