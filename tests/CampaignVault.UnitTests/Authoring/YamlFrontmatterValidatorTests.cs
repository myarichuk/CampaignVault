using System;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class YamlFrontmatterValidatorTests
{
    [Fact]
    public void ValidateDocument_ValidYaml_ReturnsNoDiagnostics()
    {
        const string content = """
            ---
            id: chars/test
            name: Test
            ---
            # Notes
            """;

        var diagnostics = YamlFrontmatterValidator.ValidateDocument(content);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidateDocument_InvalidIndent_ReportsDocumentLine()
    {
        const string content = """
            ---
            id: chars/test
              badIndent: true
            ---
            """;

        var diagnostics = YamlFrontmatterValidator.ValidateDocument(content);

        Assert.NotEmpty(diagnostics);
        Assert.Equal(3, diagnostics[0].Line);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics[0].Message));
    }

    [Fact]
    public void ValidateDocument_MissingClosingFence_ReportsError()
    {
        const string content = """
            ---
            id: chars/test
            name: Test
            """;

        var diagnostics = YamlFrontmatterValidator.ValidateDocument(content);

        Assert.Single(diagnostics);
        Assert.Contains("closing", diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateDocument_MissingOpeningFence_ReportsLineOne()
    {
        const string content = "id: chars/test\nname: Test\n";

        var diagnostics = YamlFrontmatterValidator.ValidateDocument(content);

        Assert.Single(diagnostics);
        Assert.Equal(1, diagnostics[0].Line);
    }
}