using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Server
{
    /// <summary>
    /// Runs the embedded CampaignVault server (staged under StreamingAssets by
    /// tools/embed-server.sh or the build processor) as a child process and
    /// points the client at it. The child runs as Production but loopback-only
    /// over plain HTTP (MCP_BIND_ANY=0, HTTPS_ENABLED=0), with a blank
    /// BEARER_TOKEN so no host-provided token leaks in. If the preferred port
    /// is taken it picks a free one; a pidfile lets the next launch stop a
    /// server a crashed session left behind. Killed on quit.
    /// Desktop only — WebGL cannot launch subprocesses.
    /// </summary>
    public class ServerHostManager : MonoBehaviour
    {
        public VaultClientConfig Config;
        public bool AutoStart;
        /// <summary>Preferred port; the one actually used is ActivePort.</summary>
        public int Port = 5275;
        /// <summary>Where the deployed server, pidfile, license and campaign data live. Defaults to persistentDataPath; tests point it at a temp folder.</summary>
        public string DataRoot;

        private Process _server;
        private StreamWriter _log;
        private Thread _stopping;
        private string _deployedDir = string.Empty;
        private volatile string _copyStatus;

        public bool IsRunning { get { return _server != null && !_server.HasExited; } }

        /// <summary>
        /// How long a starting server may take to answer /health before it's given up
        /// on. Generous on purpose: the first launch after an unpack pays for the OS
        /// scanning the fresh binaries and RavenDB creating its database, and giving
        /// up kills a server that was about to come up.
        /// </summary>
        public const float HealthBudgetSeconds = 180f;

        /// <summary>A server of ours from an earlier session is still running (see <see cref="EmbeddedServerSupport.OrphanAlive"/>).</summary>
        public bool HasOrphan { get { return !IsRunning && EmbeddedServerSupport.OrphanAlive(PidFile); } }
        /// <summary>The port the running server listens on (0 when stopped).</summary>
        public int ActivePort { get; private set; }
        /// <summary>The version the running server reported on /health (empty for older servers).</summary>
        public string ReportedVersion { get; private set; }

        private string Root { get { return string.IsNullOrEmpty(DataRoot) ? Application.persistentDataPath : DataRoot; } }
        private string PidFile { get { return Path.Combine(Root, "Server", EmbeddedServerSupport.PidFileName); } }
        public string LicensePath { get { return Path.Combine(Root, EmbeddedServerSupport.LicenseFileName); } }
        /// <summary>User-installed plugins: outside Server/, so client updates and payload re-syncs never touch them.</summary>
        public string UserPluginsDir { get { return Path.Combine(Root, PluginPackages.FolderName); } }
        /// <summary>Plugin ids the server is started without (one per line).</summary>
        public string DisabledPluginsFile { get { return Path.Combine(Root, PluginPackages.DisabledFileName); } }

        // On quit, only ask the server to stop: it finishes shutting RavenDB down by itself, and the
        // pidfile lets the next launch clean up if it didn't.
        private void OnApplicationQuit() { StopEmbedded(); }
        private void OnDisable() { StopEmbedded(); }

        public static string EmbeddedRid()
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            string suffix = arch == Architecture.Arm64 ? "arm64" : "x64";
#if UNITY_STANDALONE_OSX
            return "osx-" + suffix;
#elif UNITY_STANDALONE_WIN
            return "win-" + suffix;
#elif UNITY_STANDALONE_LINUX
            return "linux-" + suffix;
#else
            return null;
#endif
        }

        public static string ExeName(string rid)
        {
            return rid.StartsWith("win", StringComparison.Ordinal) ? "CampaignVault.exe" : "CampaignVault";
        }

        public static bool IsLoopbackUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out uri)) { return false; }
            string host = uri.Host;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1" || host == "::1";
        }

        /// <summary>
        /// Starts the server. status gets progress lines while it works (the
        /// first-run unpack can take a while); done gets the outcome.
        /// </summary>
        public IEnumerator StartEmbedded(Action<bool, string> done, Action<string> status = null)
        {
#if UNITY_WEBGL
            done(false, "Embedded server is not available in WebGL builds.");
            yield break;
#else
            if (Port < 1024 || Port > 65535)
            {
                done(false, "Port must be 1024-65535.");
                yield break;
            }
            if (IsRunning)
            {
                done(true, "Server already running.");
                yield break;
            }
            string rid = EmbeddedRid();
            if (string.IsNullOrEmpty(rid))
            {
                done(false, "Embedded server is desktop-only.");
                yield break;
            }
            string sourceDir = Path.Combine(PayloadRoot(), "Server", rid);
            string exeName = ExeName(rid);
            if (!File.Exists(Path.Combine(sourceDir, exeName)))
            {
                done(false, "No staged server for " + rid + ". Run tools/embed-server.sh " + rid + " before building.");
                yield break;
            }

            while (_stopping != null && _stopping.IsAlive)
            {
                if (status != null) { status("Waiting for the previous server to shut down…"); }
                yield return new WaitForSeconds(0.25f);
            }

            _deployedDir = Path.Combine(Root, "Server", rid);
            string syncFailure = null;
            string deployedDir = _deployedDir;
            string pidFile = PidFile;
            _copyStatus = null;
            var sync = new Thread(delegate ()
            {
                // A server an earlier session left running holds the port and the database lock.
                string orphan = EmbeddedServerSupport.KillOrphan(pidFile);
                if (orphan != null) { _copyStatus = orphan; Thread.Sleep(50); }
                syncFailure = SyncPayload(sourceDir, deployedDir, exeName, delegate (string line) { _copyStatus = line; });
            }) { IsBackground = true, Name = "vault-server-sync" };
            sync.Start();
            string shown = null;
            while (sync.IsAlive)
            {
                string line = _copyStatus;
                if (line != null && line != shown && status != null) { status(line); shown = line; }
                yield return null;
            }
            sync.Join();
            if (syncFailure != null)
            {
                done(false, syncFailure);
                yield break;
            }

            // RavenDB Embedded runs its database through "dotnet": without one the server dies at startup.
            string dotnetDir = EmbeddedServerSupport.FindDotnetDir();
            if (dotnetDir == null)
            {
                done(false, "The built-in server needs the .NET 10 runtime (its RavenDB database runs on it), and none was found. "
                    + "Install it from " + EmbeddedServerSupport.DotnetDownloadUrl + " and start the server again.");
                yield break;
            }

            int port = EmbeddedServerSupport.PickPort(Port, EmbeddedServerSupport.IsPortFree, EmbeddedServerSupport.FreePort);
            if (port != Port && status != null) { status("Port " + Port + " is in use; starting on " + port + " instead."); }
            Config.ServerUrl = "http://127.0.0.1:" + port;

            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(_deployedDir, exeName),
                WorkingDirectory = _deployedDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Production, but loopback-only over HTTP; blank the token and license vars so host env never leaks in.
            start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Production";
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            start.EnvironmentVariables["PATH"] = EmbeddedServerSupport.PathWithDotnet(dotnetDir, Environment.GetEnvironmentVariable("PATH"), windows);
            start.EnvironmentVariables["MCP_BIND_ANY"] = "0";
            start.EnvironmentVariables["HTTPS_ENABLED"] = "0";
            start.EnvironmentVariables["MCP_PORT"] = port.ToString(CultureInfo.InvariantCulture);
            // The client never uses gRPC sync; any free port keeps 50051 from blocking a start.
            start.EnvironmentVariables["GRPC_PORT"] = EmbeddedServerSupport.FreePort().ToString(CultureInfo.InvariantCulture);
            start.EnvironmentVariables["BEARER_TOKEN"] = string.Empty;
            start.EnvironmentVariables["CAMPAIGN_DB_PATH"] = Path.Combine(Root, "CampaignData");
            start.EnvironmentVariables["CAMPAIGN_RAVEN_LICENSE"] = string.Empty;
            start.EnvironmentVariables["CAMPAIGN_RAVEN_LICENSE_PATH"] = File.Exists(LicensePath) ? LicensePath : string.Empty;
            try { Directory.CreateDirectory(UserPluginsDir); }
            catch (IOException) { }
            start.EnvironmentVariables["CAMPAIGN_PLUGIN_DIRS"] = UserPluginsDir;
            start.EnvironmentVariables["CAMPAIGN_PLUGINS_DISABLED"] = PluginPackages.DisabledEnv(DisabledPluginsFile);

            string logPath = Path.Combine(_deployedDir, "server.log");
            int loggedLines = 0;
            var errLines = new System.Collections.Generic.List<string>();
            try
            {
                _server = new Process { StartInfo = start, EnableRaisingEvents = true };
                // Open until StopEmbedded, so what the server logs after startup lands in the file too.
                _log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                StreamWriter logRef = _log;
                _server.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e) { WriteLog(logRef, e.Data, ref loggedLines); };
                _server.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    WriteLog(logRef, e.Data == null ? null : "ERR " + e.Data, ref loggedLines);
                    if (e.Data != null) { lock (errLines) { if (errLines.Count < 40) { errLines.Add(e.Data); } } }
                };
                if (!_server.Start())
                {
                    StopEmbedded();
                    done(false, "OS refused to launch the server binary.");
                    yield break;
                }
                _server.BeginOutputReadLine();
                _server.BeginErrorReadLine();
                ActivePort = port;
                File.WriteAllText(PidFile, EmbeddedServerSupport.FormatPidFile(_server.Id, port, start.FileName));
            }
            catch (Exception ex)
            {
                StopEmbedded();
                done(false, "Launch failed: " + ex.GetType().Name);
                yield break;
            }

            if (status != null) { status("Starting the server on 127.0.0.1:" + port + "…"); }
            bool healthy = false;
            string version = string.Empty;
            float began = Time.realtimeSinceStartup;
            float nextNote = began + 10f;
            while (IsRunning && Time.realtimeSinceStartup - began < HealthBudgetSeconds)
            {
                yield return new WaitForSeconds(0.5f);
                if (status != null && Time.realtimeSinceStartup >= nextNote)
                {
                    nextNote += 10f;
                    status("Waiting for the server to answer (" + (int)(Time.realtimeSinceStartup - began) + "s; the first start can take a minute or two)…");
                }
                using (UnityWebRequest probe = UnityWebRequest.Get("http://127.0.0.1:" + port + "/health"))
                {
                    probe.timeout = 2;
                    yield return probe.SendWebRequest();
                    if (probe.result == UnityWebRequest.Result.Success
                        && EmbeddedServerSupport.ParseHealth(probe.downloadHandler.text, out version))
                    {
                        healthy = true;
                        break;
                    }
                }
            }
            if (!healthy)
            {
                bool exited = !IsRunning;
                // Let the last stderr lines land before reading them.
                if (exited) { yield return new WaitForSeconds(0.3f); }
                string why;
                lock (errLines) { why = EmbeddedServerSupport.StartupFailure(errLines); }
                StopEmbedded();
                done(false, (exited ? "The server stopped during startup" : "The server did not answer /health within " + (int)HealthBudgetSeconds + "s")
                    + (why != null ? ": " + TextSanitizer.Clean(why, 300) : ".") + " Log: " + logPath);
                yield break;
            }
            ReportedVersion = version;
            done(true, "Embedded server healthy on 127.0.0.1:" + port + (version.Length > 0 ? " (version " + version + ")" : string.Empty));
#endif
        }

        /// <summary>
        /// Asks the server to stop and returns at once; a background thread
        /// waits for RavenDB to shut down (several seconds), forces it if it
        /// hangs, then removes the pidfile. A start meanwhile waits for it.
        /// </summary>
        public void StopEmbedded()
        {
            ActivePort = 0;
            if (_server == null) { return; }
            Process server = _server;
            _server = null;
            StreamWriter log = _log;
            _log = null;
            string pidFile = PidFile;
            EmbeddedServerSupport.BeginStop(server);
            _stopping = new Thread(delegate ()
            {
                EmbeddedServerSupport.FinishStop(server, 15000);
                try { server.Dispose(); } catch (Exception) { }
                if (log != null) { lock (log) { try { log.Dispose(); } catch (Exception) { } } }
                EmbeddedServerSupport.TryDelete(pidFile);
            }) { IsBackground = true, Name = "vault-server-stop" };
            _stopping.Start();
        }

        /// <summary>Blocks until a stop in progress has finished (tests, and anything that must see the process gone).</summary>
        public bool WaitForStopped(int milliseconds)
        {
            Thread stopping = _stopping;
            return stopping == null || stopping.Join(milliseconds);
        }

        private static void WriteLog(StreamWriter log, string line, ref int count)
        {
            if (line == null || count >= 5000) { return; }
            count++;
            lock (log)
            {
                try { log.WriteLine(line); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static string PayloadRoot()
        {
            return Path.Combine(Application.streamingAssetsPath, "CampaignVault");
        }

        /// <summary>The server version this build staged (StreamingAssets/CampaignVault/Server/version.txt), or null.</summary>
        public static string ExpectedVersion()
        {
            try
            {
                string path = Path.Combine(PayloadRoot(), "Server", EmbeddedServerSupport.VersionFileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// Copies the staged payload into the data folder on first use or when
        /// the staged exe is newer. Runs off the main thread; status gets
        /// progress lines. The marker is written last, so an interrupted copy
        /// starts over next time.
        /// </summary>
        private static string SyncPayload(string sourceDir, string deployedDir, string exeName, Action<string> status)
        {
            try
            {
                string sourceExe = Path.Combine(sourceDir, exeName);
                string deployedExe = Path.Combine(deployedDir, exeName);
                string marker = Path.Combine(deployedDir, "vault-sync.txt");
                DateTime sourceTime = File.GetLastWriteTimeUtc(sourceExe);
                bool fresh = File.Exists(deployedExe)
                    && File.Exists(marker)
                    && File.ReadAllText(marker) == sourceTime.Ticks.ToString(CultureInfo.InvariantCulture);
                if (!fresh)
                {
                    long total = EmbeddedServerSupport.DirectorySize(sourceDir);
                    if (Directory.Exists(deployedDir)) { Directory.Delete(deployedDir, true); }
                    Directory.CreateDirectory(deployedDir);
                    long free = EmbeddedServerSupport.FreeBytes(deployedDir);
                    const long margin = 256L * 1024 * 1024;
                    if (free >= 0 && free < total + margin)
                    {
                        return "Not enough disk space to unpack the server: it needs " + EmbeddedServerSupport.Megabytes(total + margin)
                            + " and " + EmbeddedServerSupport.Megabytes(free) + " is free.";
                    }
                    string totalText = EmbeddedServerSupport.Megabytes(total);
                    int lastPercent = -1;
                    EmbeddedServerSupport.CopyTree(sourceDir, deployedDir, total, delegate (long copied, long all)
                    {
                        int percent = all > 0 ? (int)(copied * 100 / all) : 100;
                        if (percent == lastPercent) { return; }
                        lastPercent = percent;
                        status("Unpacking the server (first run or update): " + percent + "% of " + totalText + "…");
                    });
                    File.WriteAllText(marker, sourceTime.Ticks.ToString(CultureInfo.InvariantCulture));
                }
#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                RunChmod(deployedExe);
#endif
                return null;
            }
            catch (Exception ex)
            {
                return "Staging the server failed: " + ex.GetType().Name + " (" + ex.Message + ")";
            }
        }

#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        private static void RunChmod(string exePath)
        {
            try
            {
                using (Process chmod = Process.Start("/bin/chmod", "755 \"" + exePath + "\""))
                {
                    if (chmod != null) { chmod.WaitForExit(5000); }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Vault] chmod: " + ex.GetType().Name);
            }
        }
#endif
    }
}
