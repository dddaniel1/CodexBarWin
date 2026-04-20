using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CodexBarServiceTests
{
    private MockCacheService _mockCacheService = null!;
    private MockSettingsService _mockSettingsService = null!;
    private MockSampleDataLoader _mockSampleDataLoader = null!;
    private Mock<ILogger<CodexBarService>> _mockLogger = null!;
    private CodexBarService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockCacheService = new MockCacheService();
        _mockSettingsService = new MockSettingsService();
        _mockSampleDataLoader = new MockSampleDataLoader();
        _mockLogger = new Mock<ILogger<CodexBarService>>();

        _service = new CodexBarService(
            _mockCacheService,
            _mockSettingsService,
            _mockSampleDataLoader,
            _mockLogger.Object);
    }

    [TestMethod]
    public async Task GetUsageAsync_InvalidProvider_ReturnsNull()
    {
        // Act
        var result = await _service.GetUsageAsync("invalid");

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_NoEnabledProviders_ReturnsEmpty()
    {
        // Arrange
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = false },
            new() { Id = "codex", IsEnabled = false }
        });

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        results.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IsAvailableAsync_ReturnsTrue()
    {
        // Act
        var result = await _service.IsAvailableAsync();

        // Assert
        result.Should().BeTrue();
    }

    #region Developer Mode Tests

    [TestMethod]
    public async Task GetAllUsageAsync_DeveloperModeEnabled_UsesSampleData()
    {
        // Arrange
        _mockSettingsService.Settings.DeveloperModeEnabled = true;
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true },
            new() { Id = "gemini", IsEnabled = true }
        });

        var claudeSample = """[{"provider":"claude","usage":{"primary":{"usedPercent":45.5,"windowMinutes":60}}}]""";
        var geminiSample = """[{"provider":"gemini","usage":{"primary":{"usedPercent":32.8,"windowMinutes":60}}}]""";
        _mockSampleDataLoader.SetSampleData("claude", claudeSample);
        _mockSampleDataLoader.SetSampleData("gemini", geminiSample);

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        results.Should().HaveCount(2);
        _mockCacheService.SetCallCount.Should().Be(2); // Should cache sample data
    }

    [TestMethod]
    public async Task GetAllUsageAsync_DeveloperMode_SampleFileNotFound_ReturnsError()
    {
        // Arrange
        _mockSettingsService.Settings.DeveloperModeEnabled = true;
        _mockSettingsService.SetProviders(new List<ProviderConfig>
        {
            new() { Id = "claude", IsEnabled = true }
        });

        // Don't set sample data - simulate file not found
        _mockSampleDataLoader.SetSampleData("claude", null);

        // Act
        var results = await _service.GetAllUsageAsync();

        // Assert
        results.Should().HaveCount(1);
        results[0].Error.Should().Contain("Sample data not available");
    }

    #endregion
}
