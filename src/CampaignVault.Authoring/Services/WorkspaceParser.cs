using System;
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
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var character = _yamlDeserializer.Deserialize<Character>(yamlBlock);
        character.Notes = markdownBody;
        return character;
    }

    public Location ParseLocation(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var location = _yamlDeserializer.Deserialize<Location>(yamlBlock);
        if (string.IsNullOrWhiteSpace(location.Description))
            location.Description = markdownBody;
        return location;
    }

    public Quest ParseQuest(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var quest = _yamlDeserializer.Deserialize<Quest>(yamlBlock);
        if (string.IsNullOrWhiteSpace(quest.DmNotes))
            quest.DmNotes = markdownBody;
        return quest;
    }

    /// <summary>
    /// Extracts the YAML frontmatter block and the remaining markdown body from
    /// a file that uses the standard <c>---</c> fence convention.
    /// </summary>
    private static (string yamlBlock, string markdownBody) ExtractFrontmatter(string fileContent)
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
        return (yamlBlock, markdownBody);
    }
}

