using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;
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
        .WithTypeConverter(new FeatureDefinitionYamlConverter())
        .WithTypeConverter(new ChoiceOptionYamlConverter())
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

    /// <summary>One folder's templates by name; <c>patches:</c> files are left out (see <see cref="LoadLayer"/>).</summary>
    public IReadOnlyDictionary<string, T> Load() => LoadLayer().Templates;

    /// <summary>One folder's templates by name, plus its <c>patches:</c> files, which the caller merges after every layer has loaded.</summary>
    public (IReadOnlyDictionary<string, T> Templates, IReadOnlyList<T> Patches) LoadLayer()
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var patches = new List<T>();
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

            var template = Parse(yaml, resourceName);
            if (template?.PatchTarget != null)
                patches.Add(template);
            else if (template?.Name != null)
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

        // 3. Extract embedded defaults to disk where files are absent, and refresh extracted copies the DM
        //    never edited (disk hash still equals the hash recorded at extraction) when the shipped
        //    version changed. Without the refresh, a data fix in a new build never reaches an install
        //    that extracted the old file, because disk wins below.
        if (embeddedFiles.Count > 0)
        {
            Directory.CreateDirectory(_diskDirectory);
            foreach (var (fileName, yaml) in embeddedFiles)
            {
                var diskPath = Path.Combine(_diskDirectory, fileName);
                var shippedHash = ComputeHash(yaml);
                if (!File.Exists(diskPath))
                {
                    File.WriteAllText(diskPath, yaml);
                    manifest[fileName] = shippedHash;
                    _logger?.LogInformation("Extracted default template: {FileName} → {Path}", fileName, diskPath);
                }
                else if (manifest.TryGetValue(fileName, out var extractedHash)
                         && extractedHash != shippedHash
                         && ComputeHash(File.ReadAllText(diskPath)) == extractedHash)
                {
                    File.WriteAllText(diskPath, yaml);
                    manifest[fileName] = shippedHash;
                    _logger?.LogInformation("Updated unedited default template: {FileName} → {Path}", fileName, diskPath);
                }
            }
        }

        SaveManifest(manifest);

        // 4. Load from disk (disk files win over embedded). Enumerated in a fixed (alphabetical)
        //    order so that a name collision across multiple files — e.g. two content packs both
        //    defining "wakizashi" — resolves deterministically rather than by OS directory order.
        if (Directory.Exists(_diskDirectory))
        {
            foreach (var filePath in Directory.EnumerateFiles(_diskDirectory, "*.yaml")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var yaml = File.ReadAllText(filePath);
                var template = Parse(yaml, filePath);
                if (template?.PatchTarget != null)
                {
                    patches.Add(template);
                    continue;
                }

                if (template?.Name == null)
                    continue;

                if (result.ContainsKey(template.Name))
                {
                    _logger?.LogWarning(
                        "Content name '{Name}' in {FilePath} overrides an existing definition of the same name (last-loaded wins).",
                        template.Name, filePath);
                }

                result[template.Name] = template;
            }
        }

        return (result, patches);
    }

    // Cheap pre-check so the common file (no edits, no patch) takes the plain deserialize path unchanged.
    private static readonly Regex EditKeys = new(@"^(patches|[A-Za-z_]\w*[+-])[ \t]*:", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Deserializes one file. Top-level <c>&lt;list&gt;+:</c> / <c>&lt;list&gt;-:</c> keys become pending
    /// <see cref="RulesetTemplate.ListOps"/> (typed by deserializing them as that list), and <c>patches: &lt;name&gt;</c>
    /// marks the file as a patch. An edit naming no list property is skipped with a warning.
    /// </summary>
    internal T? Parse(string yaml, string source)
    {
        if (!EditKeys.IsMatch(yaml))
            return Deserializer.Deserialize<T>(yaml);

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return Deserializer.Deserialize<T>(yaml);

        var plain = new YamlMappingNode();
        var edits = new List<(string Key, bool Remove, YamlNode Value)>();
        string? patchTarget = null;
        foreach (var (keyNode, value) in root.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
            if (key == "patches")
                patchTarget = (value as YamlScalarNode)?.Value;
            else if (key.Length > 1 && (key[^1] == '+' || key[^1] == '-'))
                edits.Add((key[..^1].TrimEnd(), key[^1] == '-', value));
            else
                plain.Add(keyNode, value);
        }

        var template = Deserializer.Deserialize<T>(Emit(plain));
        if (template == null)
            return null;

        var ops = new List<TemplateListOp>();
        foreach (var (key, remove, value) in edits)
        {
            var prop = TemplateEdits.ListProperty(typeof(T), key);
            if (prop == null)
            {
                _logger?.LogWarning("'{Key}{Op}:' in {Source} names no list on a {Kind}; the edit is ignored.",
                    key, remove ? "-" : "+", source, typeof(T).Name);
                continue;
            }

            var holder = Deserializer.Deserialize<T>(Emit(new YamlMappingNode { { key, value } }));
            if (holder != null && prop.GetValue(holder) is System.Collections.IList items)
                ops.Add(new TemplateListOp(prop.Name, remove, items));
        }

        template.ListOps = ops;
        // A patch needs no name of its own: it takes the target's when it merges.
        if (!string.IsNullOrWhiteSpace(patchTarget))
            template.PatchTarget = patchTarget;

        return template;
    }

    private static string Emit(YamlMappingNode node)
    {
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(node)).Save(writer, assignAnchors: false);
        return writer.ToString();
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
