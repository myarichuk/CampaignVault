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
        location.Description = markdownBody;
        return location;
    }

    public Quest ParseQuest(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var quest = _yamlDeserializer.Deserialize<Quest>(yamlBlock);
        quest.DmNotes = markdownBody;
        return quest;
    }

    public Faction ParseFaction(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var faction = _yamlDeserializer.Deserialize<Faction>(yamlBlock);
        faction.Description = markdownBody;
        return faction;
    }

    public Lore ParseLore(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var lore = _yamlDeserializer.Deserialize<Lore>(yamlBlock);
        lore.Content = markdownBody;
        return lore;
    }

    public Rumor ParseRumor(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var rumor = _yamlDeserializer.Deserialize<Rumor>(yamlBlock);
        rumor.CurrentText = markdownBody;
        return rumor;
    }

    public Event ParseEvent(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var evt = _yamlDeserializer.Deserialize<Event>(yamlBlock);
        evt.Summary = markdownBody;
        return evt;
    }

    public Item ParseItem(string fileContent)
    {
        var (yamlBlock, markdownBody) = ExtractFrontmatter(fileContent);
        var item = _yamlDeserializer.Deserialize<Item>(yamlBlock);
        item.Description = markdownBody;
        return item;
    }

    /// <summary>
    /// Extracts the YAML frontmatter block and the remaining markdown body from
    /// a file that uses the standard <c>---</c> fence convention.
    /// </summary>
    public static (string yamlBlock, string markdownBody) ExtractFrontmatter(string fileContent)
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

