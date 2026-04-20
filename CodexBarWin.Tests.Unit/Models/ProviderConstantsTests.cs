using CodexBarWin.Models;
using AwesomeAssertions;

namespace CodexBarWin.Tests.Unit.Models;

[TestClass]
public class ProviderConstantsTests
{
    [TestMethod]
    public void AllowedProviders_ContainsExpectedProviders()
    {
        // Assert
        ProviderConstants.AllowedProviders.Should().Contain("claude");
        ProviderConstants.AllowedProviders.Should().Contain("codex");
        ProviderConstants.AllowedProviders.Should().Contain("gemini");
        ProviderConstants.AllowedProviders.Should().Contain("cursor");
        ProviderConstants.AllowedProviders.Should().Contain("antigravity");
        ProviderConstants.AllowedProviders.Should().Contain("openrouter");
        ProviderConstants.AllowedProviders.Should().Contain("perplexity");
        ProviderConstants.AllowedProviders.Should().HaveCount(25);
    }

    [TestMethod]
    [DataRow("claude", true)]
    [DataRow("codex", true)]
    [DataRow("gemini", true)]
    [DataRow("CLAUDE", true)]
    [DataRow("Claude", true)]
    [DataRow("invalid", false)]
    [DataRow("openrouter", true)]
    [DataRow("perplexity", true)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("  ", false)]
    public void IsValidProvider_ReturnsExpectedResult(string? providerId, bool expected)
    {
        // Act
        var result = ProviderConstants.IsValidProvider(providerId!);

        // Assert
        result.Should().Be(expected);
    }

    [TestMethod]
    [DataRow("claude", "claude")]
    [DataRow("CLAUDE", "claude")]
    [DataRow("Claude", "claude")]
    [DataRow("  claude  ", "claude")]
    [DataRow("codex", "codex")]
    [DataRow("gemini", "gemini")]
    [DataRow("OPENROUTER", "openrouter")]
    [DataRow("Perplexity", "perplexity")]
    public void ValidateAndNormalize_ValidProvider_ReturnsNormalized(string input, string expected)
    {
        // Act
        var result = ProviderConstants.ValidateAndNormalize(input);

        // Assert
        result.Should().Be(expected);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void ValidateAndNormalize_NullOrEmpty_ThrowsArgumentException(string? input)
    {
        // Act
        var action = () => ProviderConstants.ValidateAndNormalize(input!);

        // Assert
        action.Should().Throw<ArgumentException>()
            .WithMessage("*cannot be null or empty*");
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("unknown")]
    [DataRow("openai")]
    [DataRow("both")]
    public void ValidateAndNormalize_InvalidProvider_ThrowsArgumentException(string input)
    {
        // Act
        var action = () => ProviderConstants.ValidateAndNormalize(input);

        // Assert
        action.Should().Throw<ArgumentException>()
            .WithMessage($"*Invalid provider*{input}*");
    }

    [TestMethod]
    [DataRow("claude", "auto")]
    [DataRow("codex", "auto")]
    [DataRow("gemini", "auto")]
    [DataRow("antigravity", "cli")]
    [DataRow("copilot", "api")]
    [DataRow("vertexai", "oauth")]
    [DataRow("amp", "web")]
    public void GetSource_ValidProvider_ReturnsCorrectSource(string providerId, string expectedSource)
    {
        // Act
        var result = ProviderConstants.GetSource(providerId);

        // Assert
        result.Should().Be(expectedSource);
    }

    [TestMethod]
    public void GetSource_InvalidProvider_ThrowsArgumentException()
    {
        // Act
        var action = () => ProviderConstants.GetSource("invalid");

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void GetSource_CaseInsensitive()
    {
        // Act & Assert
        ProviderConstants.GetSource("CLAUDE").Should().Be("auto");
        ProviderConstants.GetSource("Claude").Should().Be("auto");
        ProviderConstants.GetSource("claude").Should().Be("auto");
    }

    [TestMethod]
    public void GetDisplayName_ValidProvider_ReturnsExpectedName()
    {
        ProviderConstants.GetDisplayName("openrouter").Should().Be("OpenRouter");
        ProviderConstants.GetDisplayName("factory").Should().Be("Droid (Factory)");
        ProviderConstants.GetDisplayName("vertexai").Should().Be("Vertex AI");
    }
}
