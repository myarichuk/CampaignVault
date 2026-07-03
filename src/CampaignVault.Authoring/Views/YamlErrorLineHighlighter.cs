using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CampaignVault.Authoring.Services;

namespace CampaignVault.Authoring.Views;

/// <summary>
/// Highlights YAML syntax error spans in the editor (underline + tinted background).
/// </summary>
public sealed class YamlErrorLineHighlighter : DocumentColorizingTransformer
{
    private IReadOnlyList<YamlDiagnostic> _diagnostics = Array.Empty<YamlDiagnostic>();

    public void SetDiagnostics(IReadOnlyList<YamlDiagnostic> diagnostics) =>
        _diagnostics = diagnostics ?? Array.Empty<YamlDiagnostic>();

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var diagnostic in _diagnostics)
        {
            if (diagnostic.Line != line.LineNumber)
            {
                continue;
            }

            try
            {
                var start = line.Offset + Math.Max(0, diagnostic.Column - 1);
                var end = Math.Min(line.EndOffset, start + Math.Max(1, diagnostic.Length));
                if (start >= line.EndOffset)
                {
                    start = line.Offset;
                    end = Math.Min(line.EndOffset, start + 1);
                }

                ChangeLinePart(start, end, element =>
                {
                    element.TextRunProperties.SetBackgroundBrush(
                        new SolidColorBrush(Color.FromArgb(48, 244, 63, 94)));
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                });
            }
            catch
            {
                // Ignore out-of-range highlights during rapid edits.
            }
        }
    }
}