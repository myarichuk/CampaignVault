using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace CampaignVault.Authoring.Services;

public sealed record YamlDiagnostic(int Line, int Column, int Length, string Message);

/// <summary>
/// Validates YAML syntax inside markdown frontmatter fences and maps errors to document line numbers.
/// </summary>
public static class YamlFrontmatterValidator
{
    public static IReadOnlyList<YamlDiagnostic> ValidateDocument(string? fileContent)
    {
        var diagnostics = new List<YamlDiagnostic>();
        if (string.IsNullOrEmpty(fileContent))
        {
            return diagnostics;
        }

        var lines = fileContent.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            diagnostics.Add(new YamlDiagnostic(
                1,
                1,
                lines.Length > 0 ? Math.Max(1, lines[0].Length) : 1,
                "Entity file must begin with a '---' YAML frontmatter fence."));
            return diagnostics;
        }

        var endIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                endIdx = i;
                break;
            }
        }

        if (endIdx < 0)
        {
            diagnostics.Add(new YamlDiagnostic(
                lines.Length,
                1,
                1,
                "Missing closing '---' frontmatter fence."));
            return diagnostics;
        }

        var yamlText = string.Join('\n', lines[1..endIdx]);
        const int yamlContentStartLine = 2;

        if (string.IsNullOrWhiteSpace(yamlText))
        {
            diagnostics.Add(new YamlDiagnostic(yamlContentStartLine, 1, 1, "Frontmatter YAML block is empty."));
            return diagnostics;
        }

        try
        {
            var parser = new Parser(new StringReader(yamlText));
            parser.Consume<StreamStart>();
            while (parser.Current is not StreamEnd)
            {
                parser.Consume<ParsingEvent>();
            }
        }
        catch (YamlException ex)
        {
            var documentLine = yamlContentStartLine + Math.Max(0, (int)ex.Start.Line - 1);
            var column = Math.Max(1, (int)ex.Start.Column);
            var length = Math.Max(1, ex.End.Column > ex.Start.Column ? (int)(ex.End.Column - ex.Start.Column) : 1);
            diagnostics.Add(new YamlDiagnostic(documentLine, column, length, ex.Message));
        }

        return diagnostics;
    }
}