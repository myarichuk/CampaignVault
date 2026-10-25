using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// The shipped content stays inside the free-rules scope (LICENSING.md): SRD 5.1 and Player Core / Player Core 2 only.
/// Product Identity (setting names, named deities and iconic characters, the named-wizard spell prefixes) must not
/// appear in the rules data, the prompts, the skills, or any string the server sends the model.
/// </summary>
public class ContentLicensingTests
{
    private static readonly Regex ProductIdentity = new(
        @"\b(waterdeep|neverwinter|faer[uû]n|baldur's gate|sword coast|forgotten realms|greyhawk|eberron|dragonlance|ravenloft" +
        @"|drizzt|strahd|elminster|zhentarim|harpers|yawning portal|undermountain|durnan|floon|xanathar|volo" +
        @"|leomund|mordenkainen|bigby|otiluke|melf's|tenser|evard|rary's|nystul|drawmij" +
        @"|beholder|mind flayer|illithid|displacer beast|githyanki|githzerai|yuan-ti|slaad|kuo-toa|carrion crawler" +
        @"|golarion|absalom|varisia|cheliax|taldor|sandpoint|magnimar|lost omens" +
        @"|sarenrae|desna|iomedae|pharasma|abadar|torag|cayden cailean|shelyn|erastil|calistria|lamashtu|norgorber|rovagug|urgathoa|zon-kuthon|gozreh" +
        @"|valeros|seoni|merisiel|ezren|amiri|harsk|seelah)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ShippedRoots =
    [
        "src/CampaignVault/RulesetData",
        "src/CampaignVault/Tools",
        "src/CampaignVault/Data",
        "src/CampaignVault/Rulesets",
        "src/CampaignVault/Services",
        "claude_skills",
        "grok",
        "plugins",
    ];

    private static readonly string[] ShippedFiles =
    [
        "recommended-system-prompt.md",
        "recommended-system-prompt.narrative.md",
        "recommended-system-prompt.opencode.md",
    ];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".yaml", ".yml", ".cs", ".md", ".json" };

    [Fact]
    public void ShippedContent_NamesNoProductIdentity()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();

        IEnumerable<string> files = ShippedFiles.Select(f => Path.Combine(root, f)).Where(File.Exists);
        foreach (var dir in ShippedRoots.Select(d => Path.Combine(root, d)).Where(Directory.Exists))
        {
            files = files.Concat(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f)))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")));
        }

        foreach (var file in files)
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                // The generators cite the Archives of Nethys by name (its domain is "aonprd"); that's attribution, not content.
                if (line.Contains("Archives of Nethys", StringComparison.Ordinal) || line.Contains("aonprd", StringComparison.Ordinal))
                {
                    continue;
                }

                var m = ProductIdentity.Match(line);
                if (m.Success)
                {
                    hits.Add($"{Path.GetRelativePath(root, file)}:{lineNo}: '{m.Value}'");
                }
            }
        }

        Assert.True(hits.Count == 0, "Product Identity in shipped content:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Pf2eFeats_ReferenceOnlyShippedClassesAndAncestries()
    {
        var root = FindRepoRoot();
        var pf2e = Path.Combine(root, "src", "CampaignVault", "RulesetData", "pf2e");
        var classes = Directory.EnumerateFiles(Path.Combine(pf2e, "classes"), "*.yaml").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var ancestries = Directory.EnumerateFiles(Path.Combine(pf2e, "ancestries"), "*.yaml").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        ancestries.UnionWith(["aiuvarin", "changeling", "dhampir", "dragonblood", "dromaar", "duskwalker", "nephilim"]);

        var classesLine = new Regex(@"^classes: \[(.*)\]", RegexOptions.Multiline);
        var ancestriesLine = new Regex(@"^ancestries: \[(.*)\]", RegexOptions.Multiline);
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(pf2e, "feats"), "*.yaml"))
        {
            var text = File.ReadAllText(file);
            var cm = classesLine.Match(text);
            if (cm.Success)
            {
                foreach (var c in cm.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!classes.Contains(c)) bad.Add($"{Path.GetFileName(file)}: class '{c}'");
                }
            }

            var am = ancestriesLine.Match(text);
            if (am.Success)
            {
                var listed = am.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (listed.Length > 0 && !listed.Any(ancestries.Contains)) bad.Add($"{Path.GetFileName(file)}: ancestries [{string.Join(", ", listed)}]");
            }
        }

        Assert.True(bad.Count == 0, "PF2e feats outside the shipped scope:\n" + string.Join("\n", bad.Take(40)));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "CampaignVault.slnx")) || File.Exists(Path.Combine(dir, "src", "CampaignVault", "CampaignVault.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
