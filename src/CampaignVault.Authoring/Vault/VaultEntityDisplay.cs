using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CampaignVault.Authoring.Vault;

public static partial class VaultEntityDisplay
{
    [GeneratedRegex(@"^(?:name|title):\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNameLineRegex();

    public static string GetDisplayName(VaultEntity entity, string? vaultPath = null)
    {
        if (!string.IsNullOrWhiteSpace(entity.RelativePath))
        {
            try
            {
                var absolute = Path.IsPathRooted(entity.RelativePath)
                    ? entity.RelativePath
                    : string.IsNullOrWhiteSpace(vaultPath)
                        ? null
                        : Path.Combine(vaultPath, entity.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (absolute != null && File.Exists(absolute))
                {
                    var fromFile = TryReadDisplayNameFromContent(File.ReadAllText(absolute));
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile!;
                }
            }
            catch
            {
            }

            return Path.GetFileNameWithoutExtension(entity.RelativePath.Replace('\\', '/'));
        }

        return entity.Id;
    }

    public static string? TryReadDisplayNameFromContent(string content)
    {
        if (!VaultFrontmatter.HasFrontmatterFence(content))
            return null;

        var match = DisplayNameLineRegex().Match(content);
        if (!match.Success)
            return null;

        var value = match.Groups[1].Value.Trim().Trim('"', '\'');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}