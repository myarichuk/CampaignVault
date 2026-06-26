using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CampaignVault.Authoring.Vault;

public static class VaultContentHash
{
    public static string Compute(string content)
    {
        var normalized = content.ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    public static string ComputeFile(string absolutePath)
    {
        var content = File.ReadAllText(absolutePath);
        return Compute(content);
    }
}