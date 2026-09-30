using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Server
{
    /// <summary>One plugin package as the server's GET /plugins reports it.</summary>
    public sealed class PluginEntry
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Version = string.Empty;
        public string Author = string.Empty;
        public string Description = string.Empty;
        /// <summary>"code" (ships a .dll) or "data" (RulesetData only).</summary>
        public string Kind = "data";
        /// <summary>"bundled" (ships with the server) or "user" (installed from a zip).</summary>
        public string Source = "bundled";
        public bool Enabled = true;
        public bool Loaded;
        public string MinEngineVersion = string.Empty;
        public readonly List<string> Systems = new List<string>();
        public readonly List<string> ModeIds = new List<string>();
        public readonly List<string> OptionKeys = new List<string>();
        public readonly List<string> Errors = new List<string>();

        public bool IsCode { get { return Kind == "code"; } }
        public bool IsUser { get { return Source == "user"; } }
    }

    /// <summary>What a plugin zip holds, checked before anything is written.</summary>
    public sealed class PluginZipInfo
    {
        public string ZipPath = string.Empty;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Version = string.Empty;
        public string Author = string.Empty;
        public string Description = string.Empty;
        public string MinEngineVersion = string.Empty;
        /// <summary>Folder inside the zip that holds plugin.json ("" when it sits at the top).</summary>
        public string Prefix = string.Empty;
        public bool IsCode;
        public readonly List<string> Dlls = new List<string>();
        public int FileCount;
        public long Bytes;
    }

    /// <summary>
    /// The plugin manager's file work, free of Unity so tests drive it on temp
    /// folders: the server's listing, the disabled-ids file the embedded server
    /// starts with, and installing a zip into the user plugins folder (zip-slip
    /// and id clashes rejected) or removing one.
    /// </summary>
    public static class PluginPackages
    {
        public const string DisabledFileName = "plugins-disabled.txt";
        public const string FolderName = "Plugins";
        /// <summary>Uncompressed size and entry caps: a zip bomb is refused before extraction.</summary>
        public const long MaxBytes = 512L * 1024 * 1024;
        public const int MaxEntries = 20000;
        private const string SdkDll = "CampaignVault.PluginSdk.dll";
        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$");

        // ------------------------------------------------------------ listing

        /// <summary>Parses GET /plugins: {"engineVersion", "plugins": [...]}.</summary>
        public static bool TryParseListing(string body, out List<PluginEntry> plugins, out string engineVersion)
        {
            plugins = new List<PluginEntry>();
            engineVersion = string.Empty;
            JsonValue json;
            if (!JsonValue.TryParse(body ?? string.Empty, out json) || json.Kind != JsonKind.Object || json.Get("plugins").Kind != JsonKind.Array) { return false; }
            engineVersion = json.GetString("engineVersion", string.Empty);
            foreach (JsonValue p in json.GetArray("plugins"))
            {
                if (p.Kind != JsonKind.Object) { continue; }
                var e = new PluginEntry
                {
                    Id = p.GetString("id", string.Empty),
                    Name = p.GetString("name", string.Empty),
                    Version = p.GetString("version", string.Empty),
                    Author = p.GetString("author", string.Empty),
                    Description = p.GetString("description", string.Empty),
                    Kind = p.GetString("kind", "data"),
                    Source = p.GetString("source", "bundled"),
                    Enabled = p.GetBool("enabled", true),
                    Loaded = p.GetBool("loaded", false),
                    MinEngineVersion = p.GetString("minEngineVersion", string.Empty),
                };
                if (e.Name.Length == 0) { e.Name = e.Id; }
                Strings(p.GetArray("systems"), e.Systems);
                Strings(p.GetArray("modeIds"), e.ModeIds);
                Strings(p.GetArray("errors"), e.Errors);
                foreach (JsonValue o in p.GetArray("campaignOptions"))
                {
                    string key = o.GetString("key", string.Empty);
                    if (key.Length > 0) { e.OptionKeys.Add(key); }
                }
                plugins.Add(e);
            }
            return true;
        }

        private static void Strings(List<JsonValue> from, List<string> into)
        {
            foreach (JsonValue v in from) { if (v.Kind == JsonKind.String && v.StringValue.Length > 0) { into.Add(v.StringValue); } }
        }

        // ------------------------------------------------------- disabled ids

        public static HashSet<string> ReadDisabled(string file)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(file)) { return ids; }
                foreach (string line in File.ReadAllLines(file))
                {
                    string id = line.Trim();
                    if (id.Length > 0 && !id.StartsWith("#", StringComparison.Ordinal)) { ids.Add(id); }
                }
            }
            catch (IOException) { }
            return ids;
        }

        /// <summary>Adds or removes id; returns false when nothing changed.</summary>
        public static bool SetDisabled(string file, string id, bool disabled)
        {
            var ids = ReadDisabled(file);
            bool changed = disabled ? ids.Add(id) : ids.Remove(id);
            if (!changed) { return false; }
            var sorted = new List<string>(ids);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "# Plugin ids the embedded server doesn't load (one per line).\n" + string.Join("\n", sorted.ToArray()) + "\n");
            return true;
        }

        /// <summary>The CAMPAIGN_PLUGINS_DISABLED value for the server's environment.</summary>
        public static string DisabledEnv(string file)
        {
            var ids = new List<string>(ReadDisabled(file));
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", ids.ToArray());
        }

        // ------------------------------------------------------------ zip check

        public static bool IsSafeId(string id) { return !string.IsNullOrEmpty(id) && SafeId.IsMatch(id); }

        /// <summary>
        /// A zip entry name as a safe relative path ('/'-separated), or false
        /// for anything that could land outside the target folder: absolute
        /// paths, drive letters, "..", and backslash tricks (zip-slip).
        /// </summary>
        public static bool TryNormalizeEntry(string name, out string relative)
        {
            relative = string.Empty;
            if (string.IsNullOrEmpty(name)) { return false; }
            string n = name.Replace('\\', '/');
            if (n.StartsWith("/", StringComparison.Ordinal) || n.IndexOf(':') >= 0 || n.IndexOf('\0') >= 0) { return false; }
            var parts = new List<string>();
            foreach (string part in n.Split('/'))
            {
                if (part.Length == 0 || part == ".") { continue; }
                if (part == "..") { return false; }
                parts.Add(part);
            }
            relative = string.Join("/", parts.ToArray());
            return relative.Length > 0;
        }

        private static bool IsJunk(string relative)
        {
            return relative.StartsWith("__MACOSX/", StringComparison.Ordinal) || relative == "__MACOSX"
                || Path.GetFileName(relative) == ".DS_Store";
        }

        /// <summary>
        /// Reads a plugin zip without extracting it: plugin.json at the top or
        /// in one folder, a safe id, every entry inside that package, no unsafe
        /// path, within the size caps. Code = it ships a .dll.
        /// </summary>
        public static bool Inspect(string zipPath, out PluginZipInfo info, out string error)
        {
            info = null;
            error = string.Empty;
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) { error = "No file at " + zipPath; return false; }
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.Entries.Count > MaxEntries) { error = "The zip holds too many files (" + zip.Entries.Count + ")."; return false; }
                    var files = new List<ZipArchiveEntry>();
                    var manifests = new List<string>();
                    long bytes = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string rel;
                        if (!TryNormalizeEntry(entry.FullName, out rel))
                        {
                            if (entry.FullName.Replace('\\', '/').Trim('/').Length == 0) { continue; }
                            error = "Refused: the zip has an unsafe path (" + entry.FullName + ").";
                            return false;
                        }
                        if (IsJunk(rel) || entry.FullName.EndsWith("/", StringComparison.Ordinal)) { continue; }
                        bytes += entry.Length;
                        files.Add(entry);
                        string lower = rel.ToLowerInvariant();
                        if (lower == "plugin.json" || (lower.EndsWith("/plugin.json", StringComparison.Ordinal) && lower.Split('/').Length == 2)) { manifests.Add(rel); }
                    }
                    if (bytes > MaxBytes) { error = "The zip unpacks to " + EmbeddedServerSupport.Megabytes(bytes) + ", more than a plugin should need."; return false; }
                    if (manifests.Count == 0) { error = "No plugin.json at the top of the zip (or in a single folder inside it)."; return false; }
                    if (manifests.Count > 1) { error = "The zip holds more than one plugin (" + string.Join(", ", manifests.ToArray()) + "). Install them one at a time."; return false; }

                    string prefix = manifests[0].Length > "plugin.json".Length ? manifests[0].Substring(0, manifests[0].Length - "plugin.json".Length) : string.Empty;
                    var result = new PluginZipInfo { ZipPath = zipPath, Prefix = prefix, Bytes = bytes };
                    foreach (ZipArchiveEntry entry in files)
                    {
                        string rel;
                        TryNormalizeEntry(entry.FullName, out rel);
                        if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { error = "The zip has files outside the plugin folder (" + rel + ")."; return false; }
                        string inner = rel.Substring(prefix.Length);
                        if (inner.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(inner), SdkDll, StringComparison.OrdinalIgnoreCase))
                        {
                            result.IsCode = true;
                            result.Dlls.Add(inner);
                        }
                        if (!string.Equals(Path.GetFileName(inner), SdkDll, StringComparison.OrdinalIgnoreCase)) { result.FileCount++; }
                    }

                    ZipArchiveEntry manifest = zip.GetEntry(manifests[0]);
                    if (manifest == null)
                    {
                        foreach (ZipArchiveEntry entry in files) { string r; TryNormalizeEntry(entry.FullName, out r); if (r == manifests[0]) { manifest = entry; } }
                    }
                    string text;
                    using (var reader = new StreamReader(manifest.Open())) { text = reader.ReadToEnd(); }
                    JsonValue json;
                    if (!JsonValue.TryParse(text, out json) || json.Kind != JsonKind.Object) { error = "plugin.json isn't valid JSON (comments and trailing commas aren't accepted here)."; return false; }
                    result.Id = json.GetString("id", string.Empty).Trim();
                    if (result.Id.Length == 0) { error = "plugin.json has no \"id\"."; return false; }
                    if (!IsSafeId(result.Id)) { error = "The plugin id \"" + result.Id + "\" can only use letters, digits, '.', '-' and '_'."; return false; }
                    result.Name = json.GetString("displayName", result.Id);
                    result.Version = json.GetString("version", "0.0.0");
                    result.Author = json.GetString("author", string.Empty);
                    result.Description = json.GetString("description", string.Empty);
                    result.MinEngineVersion = json.GetString("minEngineVersion", string.Empty);
                    info = result;
                    return true;
                }
            }
            catch (InvalidDataException) { error = "That file isn't a zip archive."; return false; }
            catch (IOException ex) { error = "Couldn't read the zip: " + ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { error = "Couldn't read the zip: " + ex.Message; return false; }
        }

        /// <summary>Why a checked zip can't be installed next to these ids on this server, or null when it can.</summary>
        public static string InstallBlocker(PluginZipInfo info, IEnumerable<string> knownIds, string engineVersion)
        {
            foreach (string id in knownIds)
            {
                if (string.Equals(id, info.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return "A plugin with the id \"" + info.Id + "\" is already installed. Uninstall it first (a bundled plugin can only be disabled).";
                }
            }
            if (!string.IsNullOrEmpty(info.MinEngineVersion) && !string.IsNullOrEmpty(engineVersion) && CompareVersions(engineVersion, info.MinEngineVersion) < 0)
            {
                return info.Name + " needs server " + info.MinEngineVersion + " or newer; this one is " + engineVersion + ".";
            }
            return null;
        }

        // ------------------------------------------------ install / uninstall

        /// <summary>
        /// Extracts the package into userDir/&lt;id&gt; through a hidden staging
        /// folder the server ignores, so a crash never leaves a half plugin.
        /// Skips a bundled PluginSdk copy and macOS metadata. Returns the folder.
        /// </summary>
        public static string Install(PluginZipInfo info, string userDir)
        {
            string target = Path.Combine(userDir, info.Id);
            if (Directory.Exists(target)) { throw new IOException("A folder named " + info.Id + " already exists in the plugins folder."); }
            Directory.CreateDirectory(userDir);
            string staging = Path.Combine(userDir, ".installing-" + Guid.NewGuid().ToString("N"));
            string stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(info.ZipPath))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string rel;
                        if (!TryNormalizeEntry(entry.FullName, out rel) || IsJunk(rel) || entry.FullName.EndsWith("/", StringComparison.Ordinal)) { continue; }
                        if (!rel.StartsWith(info.Prefix, StringComparison.OrdinalIgnoreCase)) { continue; }
                        string inner = rel.Substring(info.Prefix.Length);
                        if (inner.Length == 0 || string.Equals(Path.GetFileName(inner), SdkDll, StringComparison.OrdinalIgnoreCase)) { continue; }
                        string dest = Path.GetFullPath(Path.Combine(staging, inner.Replace('/', Path.DirectorySeparatorChar)));
                        // Belt and braces after TryNormalizeEntry: nothing lands outside the staging folder.
                        if (!dest.StartsWith(stagingFull, StringComparison.Ordinal)) { throw new IOException("Unsafe path in zip: " + entry.FullName); }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        using (Stream from = entry.Open())
                        using (FileStream to = File.Create(dest)) { from.CopyTo(to); }
                    }
                }
                Directory.Move(staging, target);
                return target;
            }
            finally
            {
                if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch (IOException) { } }
            }
        }

        /// <summary>Plugin id to folder for every package in the user plugins folder (id from plugin.json, else the folder name).</summary>
        public static Dictionary<string, string> ScanInstalled(string userDir)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(userDir) || !Directory.Exists(userDir)) { return found; }
            foreach (string dir in Directory.GetDirectories(userDir))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) { continue; }
                string id = name;
                try
                {
                    string manifest = Path.Combine(dir, "plugin.json");
                    JsonValue json;
                    if (File.Exists(manifest) && JsonValue.TryParse(File.ReadAllText(manifest), out json))
                    {
                        string declared = json.GetString("id", string.Empty).Trim();
                        if (declared.Length > 0) { id = declared; }
                    }
                }
                catch (IOException) { }
                if (!found.ContainsKey(id)) { found[id] = dir; }
            }
            return found;
        }

        /// <summary>Deletes a user-installed plugin's folder; bundled plugins aren't in userDir and can't be removed here.</summary>
        public static bool Uninstall(string userDir, string id, out string error)
        {
            error = string.Empty;
            string dir;
            if (!ScanInstalled(userDir).TryGetValue(id ?? string.Empty, out dir))
            {
                error = "No installed plugin with the id " + id + ".";
                return false;
            }
            string parent = Path.GetFullPath(userDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(dir).StartsWith(parent, StringComparison.Ordinal)) { error = "Refused: that folder isn't inside the plugins folder."; return false; }
            try
            {
                Directory.Delete(dir, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = "Couldn't remove " + dir + ": " + ex.Message + " (stop the server and try again).";
                return false;
            }
        }

        /// <summary>SemVer-ish, as the server compares minEngineVersion.</summary>
        public static int CompareVersions(string a, string b)
        {
            string[] pa = (a ?? "0").Split('.');
            string[] pb = (b ?? "0").Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                string sa = i < pa.Length ? pa[i].Trim() : "0";
                string sb = i < pb.Length ? pb[i].Trim() : "0";
                int ia, ib;
                int cmp = int.TryParse(sa, NumberStyles.Integer, CultureInfo.InvariantCulture, out ia) && int.TryParse(sb, NumberStyles.Integer, CultureInfo.InvariantCulture, out ib)
                    ? ia.CompareTo(ib)
                    : string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) { return cmp; }
            }
            return 0;
        }
    }
}
