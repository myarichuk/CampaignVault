using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Server
{
    /// <summary>
    /// Runs the embedded CampaignVault server (staged under StreamingAssets by
    /// tools/embed-server.sh or the build processor) as a child process and
    /// points the client at it. Loopback only, no token: the child starts with
    /// ASPNETCORE_ENVIRONMENT=Development so the server binds localhost, and a
    /// blank BEARER_TOKEN so no host-provided token leaks in. Killed on quit.
    /// Desktop only — WebGL cannot launch subprocesses.
    /// </summary>
    public class ServerHostManager : MonoBehaviour
    {
        public VaultClientConfig Config;
        public bool AutoStart;
        public int Port = 5275;

        private Process _server;
        private string _deployedDir = string.Empty;

        public bool IsRunning { get { return _server != null && !_server.HasExited; } }

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

        public IEnumerator StartEmbedded(Action<bool, string> done)
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

            _deployedDir = Path.Combine(Application.persistentDataPath, "Server", rid);
            string syncFailure = SyncPayload(sourceDir, _deployedDir, exeName);
            if (syncFailure != null)
            {
                done(false, syncFailure);
                yield break;
            }

            Config.ServerUrl = "http://127.0.0.1:" + Port;

            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(_deployedDir, exeName),
                WorkingDirectory = _deployedDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Loopback-only bind; blank the token so host env never leaks in.
            start.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.EnvironmentVariables["MCP_PORT"] = Port.ToString();
            start.EnvironmentVariables["MCP_BIND_ANY"] = string.Empty;
            start.EnvironmentVariables["BEARER_TOKEN"] = string.Empty;
            start.EnvironmentVariables["CAMPAIGN_DB_PATH"] = Path.Combine(Application.persistentDataPath, "CampaignData");

            string logPath = Path.Combine(_deployedDir, "server.log");
            StreamWriter log = null;
            int loggedLines = 0;
            try
            {
                _server = new Process { StartInfo = start, EnableRaisingEvents = true };
                log = new StreamWriter(logPath, false) { AutoFlush = true };
                StreamWriter logRef = log;
                _server.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || loggedLines >= 5000) { return; }
                    loggedLines++;
                    try { logRef.WriteLine(e.Data); } catch (IOException) { }
                };
                _server.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || loggedLines >= 5000) { return; }
                    loggedLines++;
                    try { logRef.WriteLine("ERR " + e.Data); } catch (IOException) { }
                };
                if (!_server.Start())
                {
                    StopEmbedded();
                    done(false, "OS refused to launch the server binary.");
                    yield break;
                }
                _server.BeginOutputReadLine();
                _server.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                if (log != null) { log.Dispose(); }
                StopEmbedded();
                done(false, "Launch failed: " + ex.GetType().Name);
                yield break;
            }

            bool healthy = false;
            for (int i = 0; i < 60 && IsRunning; i++)
            {
                yield return new WaitForSeconds(0.5f);
                using (UnityWebRequest probe = UnityWebRequest.Get("http://127.0.0.1:" + Port + "/health"))
                {
                    probe.timeout = 2;
                    yield return probe.SendWebRequest();
                    if (probe.result == UnityWebRequest.Result.Success) { healthy = true; break; }
                }
            }
            if (log != null) { log.Dispose(); }
            if (!healthy)
            {
                StopEmbedded();
                done(false, "Server did not answer /health within 30s. See " + logPath);
                yield break;
            }
            done(true, "Embedded server healthy on 127.0.0.1:" + Port);
#endif
        }

        public void StopEmbedded()
        {
            if (_server == null) { return; }
            try
            {
                if (!_server.HasExited)
                {
                    _server.Kill();
                    _server.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[Vault] Server stop: " + ex.GetType().Name);
            }
            try { _server.Dispose(); } catch (Exception) { }
            _server = null;
        }

        private static string PayloadRoot()
        {
            return Path.Combine(Application.streamingAssetsPath, "CampaignVault");
        }

        /// <summary>Copies the staged payload next to first use or when the staged exe is newer.</summary>
        private static string SyncPayload(string sourceDir, string deployedDir, string exeName)
        {
            try
            {
                string sourceExe = Path.Combine(sourceDir, exeName);
                string deployedExe = Path.Combine(deployedDir, exeName);
                DateTime sourceTime = File.GetLastWriteTimeUtc(sourceExe);
                bool fresh = File.Exists(deployedExe)
                    && File.Exists(Path.Combine(deployedDir, "vault-sync.txt"))
                    && File.ReadAllText(Path.Combine(deployedDir, "vault-sync.txt")) == sourceTime.Ticks.ToString();
                if (!fresh)
                {
                    if (Directory.Exists(deployedDir)) { Directory.Delete(deployedDir, true); }
                    CopyDirectory(sourceDir, deployedDir);
                    File.WriteAllText(Path.Combine(deployedDir, "vault-sync.txt"), sourceTime.Ticks.ToString());
                }
#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
                RunChmod(deployedExe);
#endif
                return null;
            }
            catch (Exception ex)
            {
                return "Staging the server failed: " + ex.GetType().Name + " (" + ex.Message + ")";
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

#if UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
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
