using System;
using System.Text.RegularExpressions;
using CampaignVault.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Authoring.Services;

public class WorkspaceParser
{
    private readonly IDeserializer _yamlDeserializer;

    public WorkspaceParser()
    {
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public Character ParseCharacter(string fileContent)
    {
        var match = Regex.Match(fileContent, @"^---\s*(.*?)\s*---\s*(.*)", RegexOptions.Singleline);
        if (!match.Success)
            throw new ArgumentException("Invalid frontmatter format.");

        var yamlBlock = match.Groups[1].Value;
        var markdownBody = match.Groups[2].Value.Trim();

        var character = _yamlDeserializer.Deserialize<Character>(yamlBlock);
        character.Notes = markdownBody;

        return character;
    }
}
