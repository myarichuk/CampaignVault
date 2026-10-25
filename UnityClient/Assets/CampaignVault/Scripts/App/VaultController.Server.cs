using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.App
{
    /// <summary>The server side of the client: the tool allowlist, the connection check and heartbeat, the embedded server, plugins.</summary>
    public sealed partial class VaultController
    {
        // =====================================================================
        // Tools (plugin allowlist)
        // =====================================================================

        public IEnumerator LoadTools()
        {
            if (!_s.TryBeginBusy("tools")) { yield break; }
            try
            {
                var tools = new List<ToolToggle>();
                _s.ToolsErrors.Clear();
                foreach (string connector in new[] { "play", "build" })
                {
                    McpOutcome<List<McpToolInfo>> listed = null;
                    string captured = connector;
                    yield return _s.Mcp.ListTools(_s.Config, captured, delegate (McpOutcome<List<McpToolInfo>> o) { listed = o; });
                    if (listed == null || !listed.Ok)
                    {
                        _s.ToolsErrors[captured] = listed != null ? listed.ErrorMessage : "no response";
                        continue;
                    }
                    foreach (var t in listed.Data)
                    {
                        tools.Add(new ToolToggle { Name = t.Name, Description = t.Description, Connector = captured });
                    }
                }
                _s.Tools.Clear();
                _s.Tools.AddRange(tools);
                _s.Notify(StateArea.Tools);
            }
            finally { _s.EndBusy("tools"); }
        }

        public IEnumerator ReloadTools()
        {
            _s.Driver.InvalidateTools();
            yield return LoadTools();
        }

        public void ToggleTool(string name)
        {
            var allowed = _s.Driver.AllowedTools;
            if (allowed.Count == 0)
            {
                // Empty means "everything on": seed the full list first so this
                // toggle disables just one tool.
                foreach (var t in _s.Tools) { allowed.Add(t.Name); }
            }
            if (!allowed.Remove(name)) { allowed.Add(name); }
            SaveAllowedTools();
            _s.Notify(StateArea.Tools);
        }

        private void SaveAllowedTools()
        {
            _prefs.SetString(PrefKeys.Tools, string.Join(",", new List<string>(_s.Driver.AllowedTools).ToArray()));
            _prefs.Save();
        }

        public void EnableAllTools()
        {
            _s.Driver.AllowedTools.Clear();
            SaveAllowedTools();
            _s.Notify(StateArea.Tools);
        }

        // =====================================================================
        // Server connection + embedded server
        // =====================================================================

        /// <summary>
        /// /health alone only proves the process is up; healthy here also
        /// requires an MCP session and a tools/list on /play, which is what
        /// everything else depends on.
        /// </summary>
        /// <summary>The server URL the last healthy check talked to; another URL means new sessions.</summary>
        private string _checkedUrl = string.Empty;

        /// <summary>
        /// The 30 s heartbeat (A35): a cheap /health GET while the status says Healthy and no turn is running.
        /// When the server has gone, the status says so instead of staying green until the next failed call.
        /// </summary>
        public IEnumerator PingHealth()
        {
            if (_s.Connection != ConnectionStatus.Healthy || _s.Driver == null || _s.Driver.IsBusy || _s.IsBusy("connection")) { yield break; }
            bool healthy = false;
            string message = string.Empty;
            yield return _s.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; message = msg; });
            if (healthy || _s.Connection != ConnectionStatus.Healthy) { yield break; }
            SetConnection(ConnectionStatus.Down, "The server stopped answering: " + message);
            _s.RaiseToast("The server stopped answering. Check it (or restart the built-in one) before the next turn.", ToastKind.Warning);
        }

        public IEnumerator CheckConnection()
        {
            if (!_s.TryBeginBusy("connection")) { yield break; }
            try
            {
                bool wasHealthy = _s.Connection == ConnectionStatus.Healthy && _checkedUrl == _s.Config.ServerUrl;
                _s.Connection = ConnectionStatus.Checking;
                _s.ConnectionMessage = "Checking…";
                _s.Notify(StateArea.Connection);
                bool healthy = false;
                string message = string.Empty;
                yield return _s.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; message = msg; });
                if (!healthy)
                {
                    SetConnection(ConnectionStatus.Down, "Server: " + message);
                    yield break;
                }
                // A re-check of a healthy server keeps its MCP sessions: dropping them cost every open connector a handshake (A27).
                if (!wasHealthy) { _s.Mcp.ResetSessions(); }
                _checkedUrl = _s.Config.ServerUrl;
                McpOutcome<List<McpToolInfo>> tools = null;
                yield return _s.Mcp.ListTools(_s.Config, "play", delegate (McpOutcome<List<McpToolInfo>> o) { tools = o; });
                if (tools == null || !tools.Ok)
                {
                    SetConnection(ConnectionStatus.Down, "Server is up, but MCP failed: " + (tools != null ? tools.ErrorMessage : "no response"));
                    yield break;
                }
                string versionWarning = EmbeddedServerSupport.VersionWarning(ServerHostManager.ExpectedVersion(), _s.Config.ReportedServerVersion);
                SetConnection(ConnectionStatus.Healthy, "Healthy · MCP connected (" + tools.Data.Count + " play tools)"
                    + (versionWarning != null ? " · " + versionWarning : string.Empty));
                if (versionWarning != null) { _s.RaiseToast(versionWarning, ToastKind.Warning); }
            }
            finally { _s.EndBusy("connection"); }
        }

        private void SetConnection(ConnectionStatus status, string message)
        {
            _s.Connection = status;
            _s.ConnectionMessage = message;
            _s.Notify(StateArea.Connection);
        }

        public void SetServerUrl(string url)
        {
            _s.Config.ServerUrl = (url ?? string.Empty).Trim();
            _prefs.SetString(PrefKeys.Server, _s.Config.ServerUrl);
            _prefs.Save();
            _s.Mcp.ResetSessions();
            _s.Driver.InvalidateTools();
            _s.PluginsLoaded = false;
            SetConnection(ConnectionStatus.Unknown, string.Empty);
        }

        public void SetConnector(string connector)
        {
            _s.Config.Connector = connector == "build" ? "build" : "play";
            _prefs.SetString(PrefKeys.Connector, _s.Config.Connector);
            _prefs.Save();
            _s.Driver.InvalidateTools();
            _s.Notify(StateArea.Connection);
        }

        public void SetBearerToken(string token)
        {
            _s.Config.SetBearerToken(token);
            _s.Mcp.ResetSessions();
            _s.RaiseToast("Bearer token kept in memory only.", ToastKind.Info);
        }

        public void ClearBearerToken()
        {
            _s.Config.ClearSecrets();
            _s.Mcp.ResetSessions();
            _s.RaiseToast("Bearer token cleared.", ToastKind.Info);
        }

        /// <summary>
        /// False until the launch-time autostart has either found a server,
        /// started one or given up, so the first connection check waits for it
        /// instead of racing a server that is still unpacking or booting.
        /// </summary>
        public bool AutostartSettled { get; private set; }

        /// <summary>Autostart only ever hijacks a loopback URL, never a remote server.</summary>
        public IEnumerator AutostartEmbedded()
        {
            try
            {
                if (_s.Server == null || !_s.Server.AutoStart || !ServerHostManager.IsLoopbackUrl(_s.Config.ServerUrl)) { yield break; }
                bool healthy = false;
                yield return _s.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; });
                // Healthy and not ours (a server you run yourself): use it. Healthy but an orphan
                // of an earlier session: StartEmbedded stops it and starts a fresh, owned one.
                if (healthy && !_s.Server.HasOrphan) { yield break; }
                yield return StartEmbedded(_s.Server.Port);
            }
            finally { AutostartSettled = true; }
        }

        public IEnumerator StartEmbedded(int port)
        {
            if (_s.Server == null || _s.Server.IsRunning) { yield break; }
            if (!_s.TryBeginBusy("embedded")) { yield break; }
            try
            {
                _s.Server.Port = port;
                _prefs.SetInt(PrefKeys.EmbeddedPort, port);
                _prefs.Save();
                _s.EmbeddedMessage = "Starting embedded server…";
                _s.Notify(StateArea.Embedded);
                bool started = false;
                string message = string.Empty;
                yield return _s.Server.StartEmbedded(delegate (bool ok, string msg) { started = ok; message = msg; }, delegate (string line)
                {
                    _s.EmbeddedMessage = line;
                    _s.Notify(StateArea.Embedded);
                });
                _s.EmbeddedMessage = message;
                if (started) { _s.PluginsLoaded = false; }
                else { SetConnection(ConnectionStatus.Down, "Built-in server: " + message); }
                _s.Notify(StateArea.Embedded);
                _s.RaiseToast(started ? "Embedded server started." : "The built-in server didn't start. " + message, started ? ToastKind.Success : ToastKind.Error);
            }
            finally { _s.EndBusy("embedded"); }
            // A (re)start can move the port and drops every MCP session: re-check so the
            // status and the tool list match the new server instead of the last check.
            if (_s.Server.IsRunning) { yield return CheckConnection(); }
        }

        /// <summary>A change that would pull the floor from under a running turn waits for it (A18).</summary>
        internal bool DmBusy(string what)
        {
            if (_s.Driver == null || !_s.Driver.IsBusy) { return false; }
            _s.RaiseToast("Wait for the DM's turn to finish before you " + what + ".", ToastKind.Warning);
            return true;
        }

        public void StopEmbedded()
        {
            if (_s.Server == null || DmBusy("stop the server")) { return; }
            _s.Server.StopEmbedded();
            _s.EmbeddedMessage = "Embedded server stopped.";
            _s.Notify(StateArea.Embedded);
        }

        /// <summary>
        /// Saves the RavenDB license the embedded server starts with: input is
        /// the license JSON itself or a path to a file holding it. Takes effect
        /// on the next server start.
        /// </summary>
        public bool SaveRavenLicense(string input)
        {
            if (_s.Server == null) { return false; }
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0) { _s.RaiseToast("Paste the license, or the path to its file.", ToastKind.Warning); return false; }
            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                string path = text.Trim('"');
                if (!System.IO.File.Exists(path)) { _s.RaiseToast("No license file at " + path, ToastKind.Warning); return false; }
                try { text = System.IO.File.ReadAllText(path); }
                catch (System.IO.IOException ex) { _s.RaiseToast("Couldn't read the license file: " + ex.Message, ToastKind.Warning); return false; }
            }
            string name, error;
            if (!EmbeddedServerSupport.ValidateLicense(text, out name, out error)) { _s.RaiseToast(error, ToastKind.Warning); return false; }
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_s.Server.LicensePath));
                System.IO.File.WriteAllText(_s.Server.LicensePath, text.Trim());
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _s.RaiseToast("Couldn't save the license: " + ex.Message, ToastKind.Warning);
                return false;
            }
            _s.RaiseToast("RavenDB license saved" + (name.Length > 0 ? " (" + name + ")" : string.Empty)
                + (_s.Server.IsRunning ? ". Restart the server to apply it." : "."), ToastKind.Success);
            _s.Notify(StateArea.Embedded);
            return true;
        }

        public void RemoveRavenLicense()
        {
            if (_s.Server == null) { return; }
            EmbeddedServerSupport.TryDelete(_s.Server.LicensePath);
            _s.RaiseToast("RavenDB license removed" + (_s.Server.IsRunning ? ". Restart the server to apply it." : "."), ToastKind.Info);
            _s.Notify(StateArea.Embedded);
        }

        /// <summary>Who the saved license is for, "" when it has no name, or null when none is saved.</summary>
        public string RavenLicenseHolder()
        {
            if (_s.Server == null || !System.IO.File.Exists(_s.Server.LicensePath)) { return null; }
            string name, error;
            try
            {
                return EmbeddedServerSupport.ValidateLicense(System.IO.File.ReadAllText(_s.Server.LicensePath), out name, out error) ? name : string.Empty;
            }
            catch (System.IO.IOException) { return string.Empty; }
        }

        public void SetEmbeddedAutostart(bool autostart)
        {
            if (_s.Server == null) { return; }
            _s.Server.AutoStart = autostart;
            _prefs.SetInt(PrefKeys.EmbeddedAutostart, autostart ? 1 : 0);
            _prefs.Save();
            _s.Notify(StateArea.Embedded);
        }

        // =====================================================================
        // Plugins (N6): listed from any server, managed only on the embedded one
        // =====================================================================

        /// <summary>
        /// Install, uninstall and enable/disable change the built-in server's
        /// folders, so they're offered only while connected to it (or to the
        /// loopback address it will run on, when it's stopped).
        /// </summary>
        public bool CanManagePlugins
        {
            get
            {
                if (_s.Server == null || !ServerHostManager.IsLoopbackUrl(_s.Config.ServerUrl)) { return false; }
                if (!_s.Server.IsRunning) { return true; }
                return string.Equals(_s.Config.ServerUrl.TrimEnd('/'), "http://127.0.0.1:" + _s.Server.ActivePort, StringComparison.OrdinalIgnoreCase);
            }
        }

        public IEnumerator LoadPlugins()
        {
            if (!_s.TryBeginBusy("plugins")) { yield break; }
            try
            {
                bool ok = false;
                string body = string.Empty;
                yield return _s.Config.GetText("/plugins", delegate (bool o, string b) { ok = o; body = b; });
                List<PluginEntry> plugins;
                string engine;
                if (!ok)
                {
                    _s.PluginsError = body.Contains("404") ? "This server is older than the plugin manager (no /plugins)." : "Couldn't read the server's plugins: " + body;
                }
                else if (!PluginPackages.TryParseListing(body, out plugins, out engine))
                {
                    _s.PluginsError = "The server's /plugins reply wasn't understood.";
                }
                else
                {
                    _s.Plugins.Clear();
                    _s.Plugins.AddRange(plugins);
                    _s.PluginsEngineVersion = engine;
                    _s.PluginsError = string.Empty;
                }
                _s.PluginsLoaded = true;
                _s.Notify(StateArea.Plugins);
            }
            finally { _s.EndBusy("plugins"); }
        }

        /// <summary>Enables or disables a plugin id for the next embedded server start.</summary>
        public void SetPluginEnabled(string id, bool enabled)
        {
            if (!CanManagePlugins || DmBusy("change plugins")) { return; }
            try
            {
                if (PluginPackages.SetDisabled(_s.Server.DisabledPluginsFile, id, !enabled)) { MarkPluginsChanged(id, enabled); }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _s.RaiseToast("Couldn't save the plugin list: " + ex.Message, ToastKind.Warning);
            }
        }

        public bool IsPluginDisabled(string id)
        {
            return _s.Server != null && PluginPackages.ReadDisabled(_s.Server.DisabledPluginsFile).Contains(id);
        }

        /// <summary>User-installed packages on disk (id to folder), including ones the server hasn't loaded yet.</summary>
        public Dictionary<string, string> InstalledPlugins()
        {
            return _s.Server != null ? PluginPackages.ScanInstalled(_s.Server.UserPluginsDir) : new Dictionary<string, string>();
        }

        /// <summary>
        /// Checks a zip for install: a readable package, no unsafe paths, an
        /// id not already on the server or in the plugins folder, a server
        /// new enough. Toasts the reason and returns null when it can't go in.
        /// </summary>
        public PluginZipInfo CheckPluginZip(string path)
        {
            if (!CanManagePlugins) { _s.RaiseToast("Plugins can only be installed on the built-in server.", ToastKind.Warning); return null; }
            string zip = (path ?? string.Empty).Trim().Trim('"');
            if (zip.Length == 0) { _s.RaiseToast("Enter the path to the plugin's .zip file.", ToastKind.Warning); return null; }
            PluginZipInfo info;
            string error;
            if (!PluginPackages.Inspect(zip, out info, out error)) { _s.RaiseToast(error, ToastKind.Warning); return null; }
            var known = new List<string>(InstalledPlugins().Keys);
            foreach (var p in _s.Plugins) { known.Add(p.Id); }
            string engine = _s.PluginsEngineVersion.Length > 0 ? _s.PluginsEngineVersion : ServerHostManager.ExpectedVersion();
            string blocker = PluginPackages.InstallBlocker(info, known, engine);
            if (blocker != null) { _s.RaiseToast(blocker, ToastKind.Warning); return null; }
            return info;
        }

        /// <summary>Extracts a checked zip into the user plugins folder; it loads on the next server start.</summary>
        public bool InstallPlugin(PluginZipInfo info)
        {
            if (info == null || !CanManagePlugins) { return false; }
            try
            {
                PluginPackages.Install(info, _s.Server.UserPluginsDir);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.IO.InvalidDataException)
            {
                _s.RaiseToast("Couldn't install " + info.Name + ": " + ex.Message, ToastKind.Warning);
                return false;
            }
            _s.RaiseToast(info.Name + " " + info.Version + " installed" + (_s.Server.IsRunning ? ". Restarting the server to load it…" : "; it loads when the server starts."), ToastKind.Success);
            MarkPluginsChanged(info.Id, true);
            return true;
        }

        public bool UninstallPlugin(string id)
        {
            if (!CanManagePlugins) { return false; }
            string error;
            if (!PluginPackages.Uninstall(_s.Server.UserPluginsDir, id, out error)) { _s.RaiseToast(error, ToastKind.Warning); return false; }
            try { PluginPackages.SetDisabled(_s.Server.DisabledPluginsFile, id, false); }
            catch (System.IO.IOException) { }
            _s.RaiseToast("Plugin removed" + (_s.Server.IsRunning ? ". Restarting the server to unload it…" : "."), ToastKind.Info);
            MarkPluginsChanged(id, false);
            return true;
        }

        /// <summary>
        /// Quiet time after the last plugin change before the automatic
        /// restart, so flipping several switches restarts the server once.
        /// </summary>
        public float PluginRestartDelay = 1.5f;

        private bool _pluginRestartScheduled;
        private float _pluginRestartDue;
        // What each changed plugin should look like after the restart: true = loaded.
        private readonly Dictionary<string, bool> _pluginExpectations = new Dictionary<string, bool>(StringComparer.Ordinal);

        private void MarkPluginsChanged(string id, bool expectLoaded)
        {
            _s.Prompts.Reload();
            bool running = _s.Server != null && _s.Server.IsRunning;
            _s.PluginsRestartNeeded = running;
            if (!string.IsNullOrEmpty(id)) { _pluginExpectations[id] = expectLoaded; }
            _s.Notify(StateArea.Plugins);
            if (!running) { return; }
            _pluginRestartDue = Time.realtimeSinceStartup + PluginRestartDelay;
            if (!_pluginRestartScheduled)
            {
                _pluginRestartScheduled = true;
                Run(RestartWhenQuiet());
            }
        }

        /// <summary>
        /// The automatic restart: waits out the debounce, any turn in flight
        /// (a restart mid-turn would fail its tool calls) and any other server
        /// start/stop, then restarts unless someone already pressed the button.
        /// </summary>
        private IEnumerator RestartWhenQuiet()
        {
            try
            {
                while (Time.realtimeSinceStartup < _pluginRestartDue || _s.Driver.IsBusy || _s.IsBusy("embedded"))
                {
                    yield return null;
                }
                if (!_s.PluginsRestartNeeded) { yield break; }
                yield return RestartEmbedded();
            }
            finally { _pluginRestartScheduled = false; }
        }

        /// <summary>
        /// Stops and starts the embedded server so plugin changes apply, then
        /// re-reads the list, runs the health check (/health plus an MCP
        /// tools/list) and checks each changed plugin landed as expected.
        /// </summary>
        public IEnumerator RestartEmbedded()
        {
            if (_s.Server == null) { yield break; }
            int port = _s.Server.Port;
            StopEmbedded();
            yield return StartEmbedded(port);
            if (!_s.Server.IsRunning)
            {
                _s.RaiseToast("The server didn't come back after the restart: " + _s.EmbeddedMessage, ToastKind.Warning);
                yield break;
            }
            _s.PluginsRestartNeeded = false;
            _s.Driver.InvalidateTools();
            _s.Mcp.ResetSessions();
            yield return LoadPlugins();
            yield return CheckConnection();
            if (_s.Connection != ConnectionStatus.Healthy)
            {
                _s.RaiseToast("The server restarted but failed its health check: " + _s.ConnectionMessage, ToastKind.Warning);
                yield break;
            }
            string problems = PluginExpectationProblems(_pluginExpectations, _s.Plugins);
            _pluginExpectations.Clear();
            _s.RaiseToast(problems ?? "Server restarted; plugin changes applied.", problems == null ? ToastKind.Success : ToastKind.Warning);
        }

        /// <summary>Null when every changed plugin is loaded (or gone) as intended, else what went wrong.</summary>
        public static string PluginExpectationProblems(IDictionary<string, bool> expectations, IList<PluginEntry> listed)
        {
            var problems = new List<string>();
            foreach (var pair in expectations)
            {
                PluginEntry entry = null;
                foreach (var p in listed) { if (p.Id == pair.Key) { entry = p; break; } }
                bool loaded = entry != null && entry.Loaded;
                if (pair.Value && !loaded)
                {
                    string why = entry == null ? "the server didn't find it"
                        : entry.Errors.Count > 0 ? string.Join("; ", entry.Errors.ToArray()) : "no error reported";
                    problems.Add((entry != null && entry.Name.Length > 0 ? entry.Name : pair.Key) + " didn't load: " + why);
                }
                else if (!pair.Value && loaded)
                {
                    problems.Add((entry.Name.Length > 0 ? entry.Name : pair.Key) + " is still loaded");
                }
            }
            return problems.Count == 0 ? null : string.Join(". ", problems.ToArray()) + ".";
        }

        public void OpenPluginsFolder()
        {
            if (_s.Server == null) { return; }
            try { System.IO.Directory.CreateDirectory(_s.Server.UserPluginsDir); }
            catch (System.IO.IOException) { }
            Application.OpenURL(new Uri(_s.Server.UserPluginsDir).AbsoluteUri);
        }
    }
}
