using System.Reflection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Loads YAML templates of type T from a disk directory, falling back to embedded resources.
/// On first load, extracts missing embedded defaults to disk so they are user-editable.
/// </summary>
public class RulesetTemplateLoader<T> where T : RulesetTemplate
{
    private readonly string _diskDirectory;
    private readonly Assembly _embeddedAssembly;
    private readonly string _embeddedPrefix; // e.g. "CampaignVault.RulesetData.dnd5e.pools"
    private readonly ILogger? _logger;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public RulesetTemplateLoader(
        string diskDirectory,
        Assembly embeddedAssembly,
        string embeddedResourcePrefix,
        ILogger? logger = null)
    {
        _diskDirectory = diskDirectory;
        _embeddedAssembly = embeddedAssembly;
        _embeddedPrefix = embeddedResourcePrefix.TrimEnd('.');
        _logger = logger;
    }

    public IReadOnlyDictionary<string, T> Load()
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var prefix = _embeddedPrefix + ".";

        // 1. Load from embedded resources (baseline / shipped truth)
        var embeddedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in _embeddedAssembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

            using var stream = _embeddedAssembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();

            var fileName = resourceName.Substring(prefix.Length);
            embeddedFiles[fileName] = yaml;

            var template = Deserializer.Deserialize<T>(yaml);
            if (template?.Name != null)
                result[template.Name] = template;
        }

        // 2. Extract embedded defaults to disk where files are absent
        if (embeddedFiles.Count > 0)
        {
            Directory.CreateDirectory(_diskDirectory);
            foreach (var (fileName, yaml) in embeddedFiles)
            {
                var diskPath = Path.Combine(_diskDirectory, fileName);
                if (!File.Exists(diskPath))
                {
                    File.WriteAllText(diskPath, yaml);
                    _logger?.LogInformation("Extracted default template: {FileName} → {Path}", fileName, diskPath);
                }
            }
        }

        // 3. Load from disk (disk files win over embedded)
        if (Directory.Exists(_diskDirectory))
        {
            foreach (var filePath in Directory.EnumerateFiles(_diskDirectory, "*.yaml"))
            {
                var yaml = File.ReadAllText(filePath);
                var template = Deserializer.Deserialize<T>(yaml);
                if (template?.Name != null)
                    result[template.Name] = template;
            }
        }

        return result;
    }
}
