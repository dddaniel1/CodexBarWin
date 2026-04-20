using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Service for interacting with local CLI tools natively (codex, gemini, claudecode).
/// </summary>
public class CodexBarService : ICodexBarService
{
    private readonly ICacheService _cacheService;
    private readonly ISettingsService _settingsService;
    private readonly ISampleDataLoader _sampleDataLoader;
    private readonly ILogger<CodexBarService> _logger;

    public CodexBarService(
        ICacheService cacheService,
        ISettingsService settingsService,
        ISampleDataLoader sampleDataLoader,
        ILogger<CodexBarService> logger)
    {
        _cacheService = cacheService;
        _settingsService = settingsService;
        _sampleDataLoader = sampleDataLoader;
        _logger = logger;
    }

    private async Task<(bool Success, string Output, string Error, int ExitCode)> RunLocalProcessAsync(string command, CancellationToken ct)
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

            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0, await outputTask, await errorTask, process.ExitCode);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message, -1);
        }
    }

    private string GetCliCommand(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "claude" => "claude",
            "codex" => "codex",
            "gemini" => "gemini",
            _ => provider.ToLowerInvariant()
        };
    }

    public async Task<UsageData?> GetUsageAsync(string provider, CancellationToken ct = default)
    {
        try
        {
            var normalizedProvider = ProviderConstants.ValidateAndNormalize(provider);
            var cliCmd = GetCliCommand(normalizedProvider);
            
            var result = await RunLocalProcessAsync(cliCmd, ct);

            if (!result.Success)
            {
                _logger.LogWarning("Command {Command} failed for {Provider}: {Error}", cliCmd, provider, result.Error);
                return _cacheService.Get(provider);
            }

            var data = ParseRawOutputToUsageData(result.Output, provider);

            if (data != null)
            {
                _cacheService.Set(provider, data);
            }

            return data ?? _cacheService.Get(provider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get usage for {Provider}", provider);
            return _cacheService.Get(provider);
        }
    }

    public async Task<IReadOnlyList<UsageData>> GetAllUsageAsync(CancellationToken ct = default)
    {
        var enabledProviders = _settingsService.Settings.Providers
            .Where(p => p.IsEnabled && ProviderConstants.IsValidProvider(p.Id))
            .Select(p => p.Id)
            .ToList();

        if (enabledProviders.Count == 0)
        {
            return [];
        }

        var results = new List<UsageData>();
        foreach (var id in enabledProviders)
        {
            results.Add(await FetchProviderAsync(id, ct));
        }

        Interlocked.Exchange(ref _isFirstFetch, 0);

        return results.ToList();
    }

    public async IAsyncEnumerable<UsageData> GetAllUsageStreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var enabledProviders = _settingsService.Settings.Providers
            .Where(p => p.IsEnabled && ProviderConstants.IsValidProvider(p.Id))
            .Select(p => p.Id)
            .ToList();

        if (enabledProviders.Count == 0)
        {
            yield break;
        }

        var tasks = enabledProviders
            .Select(id => FetchProviderAsync(id, ct))
            .ToList();

        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);

            var result = await completedTask;
            yield return result;
        }

        Interlocked.Exchange(ref _isFirstFetch, 0);
    }

    private int _isFirstFetch = 1;

    private async Task<UsageData> FetchProviderAsync(string provider, CancellationToken ct)
    {
        try
        {
            var normalizedProvider = ProviderConstants.ValidateAndNormalize(provider);

            if (_settingsService.Settings.DeveloperModeEnabled)
            {
                _logger.LogDebug("Developer mode: Loading sample data for {Provider}", normalizedProvider);
                
                var sampleJson = _sampleDataLoader.LoadSampleJson(normalizedProvider);
                if (!string.IsNullOrWhiteSpace(sampleJson))
                {
                    var dataList = ParseUsageJsonArray(sampleJson);
                    if (dataList.Count > 0)
                    {
                        var data = dataList[0];
                        _cacheService.Set(data.Provider, data);
                        _logger.LogDebug("Loaded sample data for {Provider}", normalizedProvider);
                        return data;
                    }
                }

                _logger.LogWarning("Sample data not available for {Provider} (Developer mode)", normalizedProvider);
                return new UsageData
                {
                    Provider = provider,
                    Error = "Sample data not available (Developer mode)",
                    FetchedAt = DateTime.UtcNow
                };
            }

            var timeoutSettings = _settingsService.Settings.Timeouts;
            var isFirstFetch = Interlocked.CompareExchange(ref _isFirstFetch, 1, 1) == 1;
            var timeout = isFirstFetch
                    ? TimeSpan.FromSeconds(timeoutSettings.CliProviderFirstFetchTimeoutSeconds)
                    : TimeSpan.FromSeconds(timeoutSettings.CliProviderTimeoutSeconds);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var cliCmd = GetCliCommand(normalizedProvider);
            var result = await RunLocalProcessAsync(cliCmd, cts.Token);

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                var data = ParseRawOutputToUsageData(result.Output, provider);
                if (data != null)
                {
                    _cacheService.Set(data.Provider, data);
                    _logger.LogDebug("Fetched {Provider} successfully", provider);
                    return data;
                }

                _logger.LogDebug("{Provider} returned empty data", provider);
                return new UsageData
                {
                    Provider = provider,
                    Error = "No valid data could be parsed from CLI output",
                    FetchedAt = DateTime.UtcNow
                };
            }

            _logger.LogDebug("{Provider} failed: {Error}", provider, result.Error);
            return new UsageData
            {
                Provider = provider,
                Error = string.IsNullOrWhiteSpace(result.Error)
                    ? $"Command failed (exit code {result.ExitCode})"
                    : result.Error.Trim(),
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{Provider} timed out", provider);
            return new UsageData
            {
                Provider = provider,
                Error = "Request timed out",
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Provider}", provider);
            return new UsageData
            {
                Provider = provider,
                Error = "Unexpected error occurred",
                FetchedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunLocalProcessAsync("Write-Output 'Native CLI Integration'", ct);
            return result.Success ? result.Output.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get version");
            return null;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return true;
    }

    private UsageData? ParseRawOutputToUsageData(string output, string provider)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        // Try JSON array first
        var list = ParseUsageJsonArray(output);
        if (list.Count > 0) return list[0];

        // Try single JSON object
        var single = ParseUsageJson(output, provider);
        if (single != null && (single.HasWeekly || single.HasTertiary || single.Session != null)) return single;

        // Manually assemble UsageData from raw output
        var match = Regex.Match(output, @"(\d+)\s*[/-]\s*(\d+)");
        int used = 0;
        int limit = 0;
        if (match.Success)
        {
            int.TryParse(match.Groups[1].Value, out used);
            int.TryParse(match.Groups[2].Value, out limit);
        }

        return new UsageData
        {
            Provider = provider,
            Status = limit > 0 ? "Parsed from CLI text" : "Raw CLI output",
            FetchedAt = DateTime.UtcNow,
            Session = limit > 0 ? new UsageWindow { Used = used, Limit = limit } : null,
            Error = limit == 0 ? "Could not parse usage data" : null
        };
    }

    private UsageData? ParseUsageJson(string json, string provider)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto);
            return dto?.ToUsageData();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private List<UsageData> ParseUsageJsonArray(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            if (json.TrimStart().StartsWith('['))
            {
                var dtos = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListUsageDataDto);
                return dtos?.Select(d => d.ToUsageData()).ToList() ?? [];
            }

            var dto = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.UsageDataDto);
            return dto != null ? [dto.ToUsageData()] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
