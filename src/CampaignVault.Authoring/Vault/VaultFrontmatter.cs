using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CampaignVault.Authoring.Vault;

public static partial class VaultFrontmatter
{
    [GeneratedRegex(@"^id:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex IdLineRegex();

    public static bool HasFrontmatterFence(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        var lines = content.ReplaceLineEndings("\n").Split('\n');
        return lines.Length > 0 && lines[0].Trim() == "---";
    }

    public static bool TryReadId(string content, out string? id)
    {
        id = null;
        if (!HasFrontmatterFence(content))
            return false;

        var match = IdLineRegex().Match(content);
        if (!match.Success)
            return false;

        id = match.Groups[1].Value.Trim().Trim('"', '\'');
        return !string.IsNullOrWhiteSpace(id);
    }

    public static string InferIdFromRelativePath(string relativePath, string entityType)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        var folder = VaultPaths.EntityFolders
            .FirstOrDefault(f => string.Equals(f.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            .Folder;

        if (string.IsNullOrEmpty(folder))
            return $"{entityType}s/{fileName}";

        var prefix = folder + "/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var subPath = normalized[prefix.Length..];
            var directory = Path.GetDirectoryName(subPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
                return $"{folder}/{directory}/{fileName}".Replace('\\', '/');

            return $"{folder}/{fileName}";
        }

        return $"{entityType}s/{fileName}";
    }
}