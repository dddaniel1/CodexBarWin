using System.Text.RegularExpressions;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Service for checking application setup status.
/// With the native HTTP service, no WSL or CLI setup is required.
/// </summary>
public partial class SetupChecker : ISetupChecker
{
    public static readonly Version MinCodexBarVersion = new("0.17.0");

    private readonly IWslService _wslService;
    private readonly ICodexBarService _codexBarService;
    private readonly ILogger<SetupChecker> _logger;

    public SetupChecker(
        IWslService wslService,
        ICodexBarService codexBarService,
        ILogger<SetupChecker> logger)
    {
        _wslService = wslService;
        _codexBarService = codexBarService;
        _logger = logger;
    }

    public Task<SetupStatus> CheckAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Native HTTP mode: no WSL required, setup check passed.");

        // Native HTTP service fetches usage directly via API.
        // No WSL or CLI installation needed — always return ready.
        return Task.FromResult(new SetupStatus
        {
            WslInstalled = true,
            WslRunning = true,
            Distros = ["native"],
            CodexBarInstalled = true,
            CodexBarVersion = "native-http",
            IsReady = true
        });
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionRegex();
}
