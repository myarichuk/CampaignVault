using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Server
{
    /// <summary>
    /// The plain parts of running the embedded server (ports, pidfile, payload
    /// copy, license file, version handshake), kept free of MonoBehaviour and
    /// Application paths so tests can drive them on a temp folder.
    /// </summary>
    public static class EmbeddedServerSupport
    {
        public const string PidFileName = "server.pid";
        public const string LicenseFileName = "raven-license.json";
        /// <summary>Written next to the staged payloads by the build/stage step: the server version this client ships with.</summary>
        public const string VersionFileName = "version.txt";
        public const string CommunityLicenseUrl = "https://ravendb.net/license/request/community";

        // ---------------------------------------------------------------- ports

        public static bool IsPortFree(int port)
        {
            if (!TryBind(IPAddress.Loopback, port)) { return false; }
            return !Socket.OSSupportsIPv6 || TryBind(IPAddress.IPv6Loopback, port);
        }

        private static bool TryBind(IPAddress address, int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(address, port);
                listener.Start();
                return true;
            }
            catch (SocketException) { return false; }
            finally { if (listener != null) { listener.Stop(); } }
        }

        /// <summary>A port the OS reports free right now (it can still be taken before the server binds it).</summary>
        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        /// <summary>The preferred port when it is free, else whatever fallback() hands out.</summary>
        public static int PickPort(int preferred, Func<int, bool> isFree, Func<int> fallback)
        {
            return isFree(preferred) ? preferred : fallback();
        }

        // -------------------------------------------------------------- pidfile

        public static string FormatPidFile(int pid, int port, string exePath)
        {
            return pid.ToString(CultureInfo.InvariantCulture) + "\n" + port.ToString(CultureInfo.InvariantCulture) + "\n" + exePath + "\n";
        }

        public static bool TryParsePidFile(string text, out int pid, out int port, out string exePath)
        {
            pid = 0;
            port = 0;
            exePath = string.Empty;
            string[] lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            if (lines.Length < 3) { return false; }
            if (!int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0) { return false; }
            if (!int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) { return false; }
            exePath = lines[2].Trim();
            return exePath.Length > 0;
        }

        /// <summary>
        /// Stops the server a crashed or quit client left behind, if the pidfile
        /// names a live process that is our server binary (never an unrelated
        /// process that reused the pid). Deletes the pidfile. Blocks up to ~20s:
        /// call it off the main thread. Returns what happened, or null when
        /// there was nothing to do.
        /// </summary>
        public static string KillOrphan(string pidFile)
        {
            if (!File.Exists(pidFile)) { return null; }
            string text;
            try { text = File.ReadAllText(pidFile); }
            catch (IOException) { return null; }
            TryDelete(pidFile);
            int pid, port;
            string exe;
            if (!TryParsePidFile(text, out pid, out port, out exe)) { return null; }
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited || !IsSameBinary(p, exe)) { return null; }
                    BeginStop(p);
                    FinishStop(p, 15000);
                    return "Stopped a server left running by an earlier session (pid " + pid + ", port " + port + ").";
                }
            }
            catch (ArgumentException) { return null; }          // not running
            catch (InvalidOperationException) { return null; }  // exited meanwhile
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        /// <summary>
        /// First half of stopping the server and the RavenDB process it runs as
        /// a child, cheap enough for the main thread. Unix: SIGTERM, so the
        /// server shuts RavenDB down and releases the database lock (a plain
        /// Kill() leaves RavenDB holding it, and the next start can't open the
        /// data). Windows: taskkill the whole tree.
        /// </summary>
        public static void BeginStop(Process p)
        {
            try
            {
                if (p.HasExited) { return; }
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { RunTool("taskkill", "/PID " + p.Id + " /T /F"); }
                else { RunTool("/bin/kill", "-TERM " + p.Id); }
            }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Second half, blocking (run it off the main thread): waits graceMs for
        /// the graceful exit, then SIGKILLs the children and the server
        /// (children first: once the parent is gone they can't be found by it).
        /// </summary>
        public static void FinishStop(Process p, int graceMs)
        {
            try
            {
                if (p.WaitForExit(graceMs)) { return; }
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RunTool(File.Exists("/usr/bin/pkill") ? "/usr/bin/pkill" : "pkill", "-KILL -P " + p.Id);
                }
                if (!p.HasExited) { p.Kill(); }
                p.WaitForExit(3000);
            }
            catch (InvalidOperationException) { }             // already gone
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void RunTool(string file, string args)
        {
            try
            {
                using (Process tool = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (tool != null) { tool.WaitForExit(5000); }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Same binary by file name, not full path: symlinked folders (macOS
        /// /var → /private/var) make the paths differ for the same file. Only
        /// a pid reused by another CampaignVault server could be mistaken.
        /// </summary>
        private static bool IsSameBinary(Process p, string exe)
        {
            string expected = Path.GetFileNameWithoutExtension(exe);
            try
            {
                string path = p.MainModule != null ? p.MainModule.FileName : null;
                if (!string.IsNullOrEmpty(path)) { return string.Equals(Path.GetFileNameWithoutExtension(path), expected, StringComparison.OrdinalIgnoreCase); }
            }
            catch (Exception) { }
            // MainModule isn't always readable (permissions, Mono on macOS): fall back to the process name.
            try { return string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.Delete(path); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------- payload copy

        public static long DirectorySize(string dir)
        {
            long total = 0;
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) { total += new FileInfo(file).Length; }
            return total;
        }

        /// <summary>Free bytes on the volume holding dir, or -1 when the platform won't say.</summary>
        public static long FreeBytes(string dir)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(root)) { return -1; }
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>Recursive copy that reports (copied, total) bytes after each file.</summary>
        public static void CopyTree(string source, string target, long total, Action<long, long> progress)
        {
            long copied = 0;
            CopyTree(source, target, total, progress, ref copied);
        }

        private static void CopyTree(string source, string target, long total, Action<long, long> progress, ref long copied)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                copied += new FileInfo(file).Length;
                if (progress != null) { progress(copied, total); }
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)), total, progress, ref copied);
            }
        }

        public static string Megabytes(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        // ------------------------------------------------------ .NET runtime

        public const string DotnetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

        /// <summary>
        /// The folder holding a dotnet host, or null. RavenDB Embedded runs its
        /// database server through "dotnet", so the self-contained CampaignVault
        /// still needs one installed. An app started from the Dock, Finder or
        /// Unity Hub gets a bare PATH without the installer's folders, so the
        /// usual install locations are searched after DOTNET_ROOT and PATH.
        /// </summary>
        public static string FindDotnetDir(string dotnetRoot, string path, string home, bool windows, Func<string, bool> exists)
        {
            string exe = windows ? "dotnet.exe" : "dotnet";
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(dotnetRoot)) { candidates.Add(dotnetRoot); }
            foreach (string dir in (path ?? string.Empty).Split(windows ? ';' : ':'))
            {
                if (dir.Trim().Length > 0) { candidates.Add(dir.Trim()); }
            }
            if (windows)
            {
                candidates.Add(@"C:\Program Files\dotnet");
                candidates.Add(@"C:\Program Files (x86)\dotnet");
                if (!string.IsNullOrEmpty(home)) { candidates.Add(Path.Combine(home, ".dotnet")); }
            }
            else
            {
                candidates.Add("/usr/local/share/dotnet");
                candidates.Add("/opt/homebrew/bin");
                candidates.Add("/usr/local/bin");
                candidates.Add("/usr/share/dotnet");
                candidates.Add("/usr/lib/dotnet");
                candidates.Add("/snap/bin");
                if (!string.IsNullOrEmpty(home)) { candidates.Add(Path.Combine(home, ".dotnet")); }
            }
            foreach (string dir in candidates)
            {
                if (exists(Path.Combine(dir, exe))) { return dir; }
            }
            return null;
        }

        /// <summary>This machine's dotnet folder, or null.</summary>
        public static string FindDotnetDir()
        {
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            return FindDotnetDir(
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                windows,
                File.Exists);
        }

        /// <summary>The PATH to give the server so it (and RavenDB under it) finds dotnet first.</summary>
        public static string PathWithDotnet(string dotnetDir, string path, bool windows)
        {
            if (string.IsNullOrEmpty(dotnetDir)) { return path ?? string.Empty; }
            return string.IsNullOrEmpty(path) ? dotnetDir : dotnetDir + (windows ? ";" : ":") + path;
        }

        /// <summary>
        /// Why the server died at startup, from its log lines: the unhandled
        /// exception's message when there is one, else the last error line.
        /// </summary>
        public static string StartupFailure(System.Collections.Generic.IList<string> errLines)
        {
            if (errLines == null || errLines.Count == 0) { return null; }
            foreach (string line in errLines)
            {
                int at = line.IndexOf("Unhandled exception.", StringComparison.Ordinal);
                if (at >= 0) { return line.Substring(at + "Unhandled exception.".Length).Trim(); }
            }
            return errLines[errLines.Count - 1].Trim();
        }

        // ------------------------------------------------------ build guards

        /// <summary>True for a git-lfs pointer stub (a clone without git-lfs) instead of the real file.</summary>
        public static bool IsLfsPointer(string path)
        {
            if (!File.Exists(path)) { return false; }
            var buffer = new byte[64];
            int read;
            using (FileStream f = File.OpenRead(path)) { read = f.Read(buffer, 0, buffer.Length); }
            return Encoding.ASCII.GetString(buffer, 0, read).StartsWith("version https://git-lfs", StringComparison.Ordinal);
        }

        /// <summary>The version constant from src/CampaignVault/Plugins/EngineVersion.cs, or null.</summary>
        public static string ParseEngineVersion(string engineVersionSource)
        {
            Match m = Regex.Match(engineVersionSource ?? string.Empty, "Current\\s*=\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------------------------------------------------- version handshake

        /// <summary>Parses GET /health: healthy when status is "healthy"; version is empty for servers older than the handshake.</summary>
        public static bool ParseHealth(string body, out string version)
        {
            version = string.Empty;
            JsonValue json;
            if (!JsonValue.TryParse(body ?? string.Empty, out json) || json.GetString("status", string.Empty) != "healthy") { return false; }
            version = json.GetString("version", string.Empty);
            return true;
        }

        /// <summary>A warning when the server isn't the version this client was built with; null when they match or either is unknown to the client.</summary>
        public static string VersionWarning(string expected, string reported)
        {
            if (string.IsNullOrEmpty(expected)) { return null; }
            if (string.IsNullOrEmpty(reported))
            {
                return "The server doesn't report its version (older than " + expected + "); some features may be missing.";
            }
            if (reported == expected) { return null; }
            return "Server version " + reported + ", but this client was built with " + expected + ". Tools may not match.";
        }

        // ------------------------------------------------------ RavenDB license

        /// <summary>
        /// Checks a RavenDB license the user pasted: a JSON object with an Id
        /// and a non-empty Keys array. name gets the licensee (may be empty).
        /// Only the shape is checked; RavenDB validates the keys on start.
        /// </summary>
        public static bool ValidateLicense(string text, out string name, out string error)
        {
            name = string.Empty;
            error = string.Empty;
            JsonValue json;
            if (!JsonValue.TryParse((text ?? string.Empty).Trim(), out json) || json.Kind != JsonKind.Object)
            {
                error = "That isn't a RavenDB license: paste the whole JSON block, braces included.";
                return false;
            }
            if (string.IsNullOrEmpty(json.GetString("Id", string.Empty)) || json.GetArray("Keys").Count == 0)
            {
                error = "That JSON has no license Id and Keys; paste the license exactly as RavenDB sent it.";
                return false;
            }
            name = json.GetString("Name", string.Empty);
            return true;
        }
    }
}
