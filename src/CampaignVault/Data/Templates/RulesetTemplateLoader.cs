using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Loads YAML templates of type T from a disk directory, falling back to embedded resources.
/// On first load, extracts missing embedded defaults to disk so they are user-editable.
/// Tracks extracted files in a manifest so defaults removed from a later build are pruned from
/// disk instead of persisting forever, while files a DM has edited locally are left alone.
/// </summary>
public class RulesetTemplateLoader<T> where T : RulesetTemplate
{
    private const string ManifestFileName = ".extracted-manifest.json";

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

        // 2. Prune on-disk files this loader previously extracted whose embedded source is now
        //    gone, unless the DM has edited the file locally (hash mismatch), in which case it's
        //    treated as homebrew and left alone.
        var manifest = LoadManifest();
        if (Directory.Exists(_diskDirectory))
        {
            foreach (var (fileName, extractedHash) in manifest.ToList())
            {
                if (embeddedFiles.ContainsKey(fileName))
                    continue;

                var diskPath = Path.Combine(_diskDirectory, fileName);
                if (!File.Exists(diskPath))
                {
                    manifest.Remove(fileName);
                    continue;
                }

                if (ComputeHash(File.ReadAllText(diskPath)) == extractedHash)
                {
                    File.Delete(diskPath);
                    _logger?.LogInformation("Pruned stale extracted template: {FileName} (no longer shipped)", fileName);
                }
                else
                {
                    _logger?.LogInformation(
                        "Extracted template {FileName} was modified locally; treating as homebrew (no longer tracked for pruning).",
                        fileName);
                }

                manifest.Remove(fileName);
            }
        }

        // 3. Extract embedded defaults to disk where files are absent
        if (embeddedFiles.Count > 0)
        {
            Directory.CreateDirectory(_diskDirectory);
            foreach (var (fileName, yaml) in embeddedFiles)
            {
                var diskPath = Path.Combine(_diskDirectory, fileName);
                if (!File.Exists(diskPath))
                {
                    File.WriteAllText(diskPath, yaml);
                    manifest[fileName] = ComputeHash(yaml);
                    _logger?.LogInformation("Extracted default template: {FileName} → {Path}", fileName, diskPath);
                }
            }
        }

        SaveManifest(manifest);

        // 4. Load from disk (disk files win over embedded)
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

    private string ManifestPath => Path.Combine(_diskDirectory, ManifestFileName);

    private Dictionary<string, string> LoadManifest()
    {
        if (!File.Exists(ManifestPath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(ManifestPath);
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return deserialized != null
                ? new Dictionary<string, string>(deserialized, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger?.LogWarning(ex, "Failed to read extraction manifest at {Path}; treating as empty.", ManifestPath);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveManifest(Dictionary<string, string> manifest)
    {
        if (!Directory.Exists(_diskDirectory))
            return;

        if (manifest.Count == 0)
        {
            File.Delete(ManifestPath);
            return;
        }

        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
