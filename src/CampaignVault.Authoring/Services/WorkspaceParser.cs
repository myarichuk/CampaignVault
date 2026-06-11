using System;
using System.Text.RegularExpressions;
using CampaignVault.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Authoring.Services;

public class WorkspaceParser
{
    private static readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public WorkspaceParser()
    {
    }

    public Character ParseCharacter(string fileContent)
    {
        var lines = fileContent.ReplaceLineEndings("\n").Split('\n');
        int start = -1, end = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                if (start == -1) start = i;
                else { end = i; break; }
            }
        }

        if (start == -1 || end == -1)
            throw new ArgumentException("Invalid frontmatter format.");

        var yamlBlock = string.Join('\n', lines[(start + 1)..end]);
        var markdownBody = string.Join('\n', lines[(end + 1)..]).Trim();

        var character = _yamlDeserializer.Deserialize<Character>(yamlBlock);
        character.Notes = markdownBody;

        return character;
    }
}
