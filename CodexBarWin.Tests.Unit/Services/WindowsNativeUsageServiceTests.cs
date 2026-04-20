using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class WindowsNativeUsageServiceTests
{
    private static readonly SemaphoreSlim PathMutationLock = new(1, 1);

    [TestMethod]
    public void CopilotQuotaToUsageWindow_MapsPercentRemainingToUsedPercent()
    {
        using var doc = JsonDocument.Parse("""
        {
          "percentRemaining": 72,
          "isPlaceholder": false
        }
        """);

        var window = InvokeStatic<UsageWindow?>("CopilotQuotaToUsageWindow", doc.RootElement);

        window.Should().NotBeNull();
        window!.Used.Should().Be(28);
        window.Limit.Should().Be(100);
    }

    [TestMethod]
    public void JsonLimitToUsageWindow_MapsPercentageAndResetTimestamp()
    {
        using var doc = JsonDocument.Parse("""
        {
          "percentage": 42,
          "nextResetTime": 1760000000000
        }
        """);

        var window = InvokeStatic<UsageWindow>("JsonLimitToUsageWindow", doc.RootElement);

        window.Used.Should().Be(42);
        window.Limit.Should().Be(100);
        window.ResetAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000).UtcDateTime);
    }

    [TestMethod]
    public void ParseWarpBonusCredits_AggregatesUserAndWorkspaceCredits()
    {
        using var doc = JsonDocument.Parse("""
        {
          "bonusGrants": [
            { "requestCreditsGranted": 20, "requestCreditsRemaining": 5, "expiration": "2026-05-01T00:00:00Z" }
          ],
          "workspaces": [
            {
              "bonusGrantsInfo": {
                "grants": [
                  { "requestCreditsGranted": 10, "requestCreditsRemaining": 3, "expiration": "2026-04-20T00:00:00Z" }
                ]
              }
            }
          ]
        }
        """);

        var result = InvokeStatic<(int Granted, int Remaining, DateTime? Expiration)>("ParseWarpBonusCredits", doc.RootElement);

        result.Granted.Should().Be(30);
        result.Remaining.Should().Be(8);
        result.Expiration.Should().Be(DateTime.Parse("2026-04-20T00:00:00Z").ToUniversalTime());
    }

    [TestMethod]
    public void ExtractCookieValue_ReassemblesChunkedPerplexityCookie()
    {
        var value = InvokeStatic<string?>(
            "ExtractCookieValue",
            "foo=1; __Secure-next-auth.session-token.0=abc; __Secure-next-auth.session-token.1=def; bar=2",
            "__Secure-next-auth.session-token");

        value.Should().Be("abcdef");
    }

    [TestMethod]
    public void ExtractJsObject_ReadsAmpFreeTierUsageObject()
    {
        var html = "<script>const freeTierUsage = { quota: 50, used: 12, hourlyReplenishment: 2 };</script>";

        var result = InvokeStatic<string?>("ExtractJsObject", html, "freeTierUsage");

        result.Should().NotBeNull();
        result.Should().Contain("quota: 50");
        result.Should().Contain("used: 12");
    }

    [TestMethod]
    public void NormalizeWorkspaceId_ExtractsWorkspaceIdFromUrl()
    {
        var result = InvokeStatic<string?>(
            "NormalizeWorkspaceId",
            "https://opencode.ai/workspace/wrk_123ABC/billing");

        result.Should().Be("wrk_123ABC");
    }

    [TestMethod]
    public void ExtractXmlOptionValue_ReturnsJetBrainsOptionValue()
    {
        var xml = "<component name=\"AIAssistantQuotaManager2\"><option name=\"quotaInfo\" value=\"{&quot;current&quot;:&quot;10&quot;,&quot;maximum&quot;:&quot;100&quot;}\" /></component>";

        var result = InvokeStatic<string?>("ExtractXmlOptionValue", xml, "quotaInfo");

        result.Should().Contain("&quot;current&quot;");
        result.Should().Contain("&quot;maximum&quot;");
    }

    [TestMethod]
    public async Task FetchAntigravityAsync_ExitZeroWithEmptyOutput_DoesNotReportCliFailure()
    {
        await PathMutationLock.WaitAsync();
        var commandDirectory = CreateCommandDirectory("@echo off\r\nexit /b 0\r\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{commandDirectory};{originalPath}");
            var service = CreateService();

            var result = await InvokeInstanceAsync<UsageData>(service, "FetchAntigravityAsync", CancellationToken.None);

            result.Provider.Should().Be("antigravity");
            result.Error.Should().MatchRegex("Antigravity .* but could not be probed.");
            result.Error.Should().NotContain("Make sure the 'antigravity' command is installed and in PATH.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(commandDirectory, recursive: true);
            PathMutationLock.Release();
        }
    }

    [TestMethod]
    public async Task FetchAntigravityAsync_NonZeroExitWithoutStderr_ReportsCliFailure()
    {
        await PathMutationLock.WaitAsync();
        var commandDirectory = CreateCommandDirectory("@echo off\r\nexit /b 1\r\n");
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{commandDirectory};{originalPath}");
            var service = CreateService();

            var result = await InvokeInstanceAsync<UsageData>(service, "FetchAntigravityAsync", CancellationToken.None);

            result.Provider.Should().Be("antigravity");
            result.Error.Should().MatchRegex("Antigravity .* but could not be probed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(commandDirectory, recursive: true);
            PathMutationLock.Release();
        }
    }

    private static T InvokeStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(WindowsNativeUsageService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull($"Expected private static method {methodName} to exist");

        var result = method!.Invoke(null, args);
        return (T)result!;
    }

    private static async Task<T> InvokeInstanceAsync<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull($"Expected private instance method {methodName} to exist");

        var task = method!.Invoke(instance, args);
        task.Should().BeAssignableTo<Task<T>>();
        return await (Task<T>)task!;
    }

    private static WindowsNativeUsageService CreateService()
    {
        return new WindowsNativeUsageService(
            new MockCacheService(),
            new MockSettingsService(),
            new MockSampleDataLoader(),
            NullLogger<WindowsNativeUsageService>.Instance);
    }

    private static string CreateCommandDirectory(string scriptContent)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CodexBarWinTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "antigravity.cmd"), scriptContent);
        return directory;
    }
}
