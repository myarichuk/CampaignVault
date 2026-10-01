using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Server;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>One plugin the server listed (Templates/Settings/PluginCard.uxml), kept per id across refreshes.</summary>
    public sealed class PluginCardViewModel : ViewModel, IKeyed
    {
        private readonly VaultController _c;
        private bool _enabled;

        public PluginCardViewModel(string id, VaultController controller, Action uninstall)
        {
            Key = id;
            Name = "plugin-" + id;
            _c = controller;
            Uninstall = uninstall;
        }

        private string _title = string.Empty;
        private string _meta = string.Empty;
        private string _description = string.Empty;
        private string _adds = string.Empty;
        private List<string> _errors = new List<string>();
        private string _state = string.Empty;
        private string _stateTone = "muted";
        private bool _canManage;
        private bool _canUninstall;

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string Meta { get { return _meta; } private set { Set(ref _meta, value); } }
        [CreateProperty] public string Description { get { return _description; } private set { Set(ref _description, value); } }
        [CreateProperty] public string Adds { get { return _adds; } private set { Set(ref _adds, value); } }
        [CreateProperty] public List<string> Errors { get { return _errors; } private set { SetList(ref _errors, value); } }
        [CreateProperty] public string State { get { return _state; } private set { Set(ref _state, value); } }
        [CreateProperty] public string StateTone { get { return _stateTone; } private set { Set(ref _stateTone, value); } }
        /// <summary>The plugin can be switched from here (the built-in server only).</summary>
        [CreateProperty] public bool CanManage { get { return _canManage; } private set { Set(ref _canManage, value); } }
        [CreateProperty] public bool CanUninstall { get { return _canUninstall; } private set { Set(ref _canUninstall, value); } }
        [CreateProperty] public Action Uninstall { get; private set; }

        /// <summary>What the server will load; flipping it restarts the server shortly.</summary>
        [CreateProperty]
        public bool Enabled
        {
            get { return _enabled; }
            set { if (Set(ref _enabled, value)) { _c.SetPluginEnabled(Key, value); } }
        }

        public void Update(PluginEntry plugin, bool manage)
        {
            Set(ref _enabled, !_c.IsPluginDisabled(plugin.Id), "Enabled");
            var meta = new List<string>();
            if (plugin.Author.Length > 0) { meta.Add("by " + plugin.Author); }
            meta.Add(plugin.IsCode ? "Code plugin" : "Data only");
            meta.Add(plugin.IsUser ? "Installed by you" : "Bundled");
            if (plugin.Systems.Count > 0) { meta.Add("for " + string.Join(", ", plugin.Systems.ToArray())); }
            var adds = new List<string>();
            if (plugin.ModeIds.Count > 0) { adds.Add("modes: " + string.Join(", ", plugin.ModeIds.ToArray())); }
            if (plugin.OptionKeys.Count > 0) { adds.Add("campaign options: " + string.Join(", ", plugin.OptionKeys.ToArray())); }
            var errors = new List<string>();
            foreach (string error in plugin.Errors) { errors.Add(DisplayText.Plain(error)); }
            Title = DisplayText.Plain(plugin.Name + (plugin.Version.Length > 0 ? "  ·  v" + plugin.Version : string.Empty)).ToUpperInvariant();
            Meta = DisplayText.Plain(string.Join("  ·  ", meta.ToArray()));
            Description = DisplayText.Plain(plugin.Description);
            Adds = adds.Count > 0 ? DisplayText.Plain("Adds " + string.Join("; ", adds.ToArray())) : string.Empty;
            Errors = errors;
            State = !plugin.Enabled ? "Disabled." : plugin.Loaded ? "Loaded." : "Not loaded.";
            StateTone = plugin.Loaded ? "leaf" : "muted";
            CanManage = manage;
            CanUninstall = manage && plugin.IsUser;
        }
    }

    /// <summary>A plugin on disk the server hasn't loaded yet (installed since it started, or while it was stopped).</summary>
    public sealed class OfflinePluginViewModel : ViewModel, IKeyed
    {
        public OfflinePluginViewModel(string id, string folder, Action uninstall)
        {
            Key = id;
            Title = DisplayText.Plain(id).ToUpperInvariant();
            Note = DisplayText.Plain("Installed in " + folder + ". The server loads it on its next start.");
            Uninstall = uninstall;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public string Note { get; private set; }
        [CreateProperty] public Action Uninstall { get; private set; }
    }

    /// <summary>Installing and managing server plugins.</summary>
    public sealed class PluginsPage : SettingsPage
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly ConfirmDialog _confirm;
        private List<PluginCardViewModel> _plugins = new List<PluginCardViewModel>();
        private List<OfflinePluginViewModel> _offline = new List<OfflinePluginViewModel>();
        private List<string> _errors = new List<string>();
        private bool _manage;
        private bool _restart;
        private string _restartTitle = string.Empty;
        private string _restartLabel = string.Empty;
        private string _intro = string.Empty;
        private string _zipPath = string.Empty;
        private string _notice = string.Empty;

        public PluginsPage(VaultAppState state, VaultController controller, ConfirmDialog confirm)
        {
            _s = state;
            _c = controller;
            _confirm = confirm;
            RestartNow = delegate { _c.Run(_c.RestartEmbedded()); };
            Install = delegate { BeginInstall(_zipPath); };
            OpenFolder = delegate { _c.OpenPluginsFolder(); };
            ReloadList = delegate { _c.Run(_c.LoadPlugins()); };
            Watch(state, StateArea.Plugins | StateArea.Embedded | StateArea.Busy);
            Refresh();
        }

        public override string Template { get { return "Settings/PluginsPage"; } }

        [CreateProperty] public List<PluginCardViewModel> Plugins { get { return _plugins; } private set { SetList(ref _plugins, value); } }
        [CreateProperty] public List<OfflinePluginViewModel> Offline { get { return _offline; } private set { SetList(ref _offline, value); } }
        /// <summary>Why the plugin list couldn't be read.</summary>
        [CreateProperty] public List<string> Errors { get { return _errors; } private set { SetList(ref _errors, value); } }
        [CreateProperty] public bool Manage { get { return _manage; } private set { Set(ref _manage, value); } }
        [CreateProperty] public bool ShowRestart { get { return _restart; } private set { Set(ref _restart, value); } }
        [CreateProperty] public string RestartTitle { get { return _restartTitle; } private set { Set(ref _restartTitle, value); } }
        [CreateProperty] public string RestartLabel { get { return _restartLabel; } private set { Set(ref _restartLabel, value); } }
        [CreateProperty] public string Intro { get { return _intro; } private set { Set(ref _intro, value); } }
        [CreateProperty] public string ZipPath { get { return _zipPath; } set { Set(ref _zipPath, value ?? string.Empty); } }
        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public Action RestartNow { get; private set; }
        [CreateProperty] public Action Install { get; private set; }
        [CreateProperty] public Action OpenFolder { get; private set; }
        [CreateProperty] public Action ReloadList { get; private set; }

        public override void Enter()
        {
            if (!_s.PluginsLoaded && !_s.IsBusy("plugins")) { _c.Run(_c.LoadPlugins()); }
        }

        public override void Refresh()
        {
            bool manage = _c.CanManagePlugins;
            var server = _s.Server;
            bool running = server != null && server.IsRunning;
            bool busy = _s.IsBusy("embedded");
            Manage = manage;
            ShowRestart = manage && _s.PluginsRestartNeeded;
            RestartTitle = busy ? "RESTARTING THE SERVER" : "RESTART COMING";
            RestartLabel = busy ? "RESTARTING…" : "RESTART NOW";
            Intro = !manage ? "Plugins on the server you're connected to. Manage them on the machine that runs it."
                : running ? "Plugins the built-in server found at its last start."
                : "The built-in server isn't running: plugins installed here load when it starts.";
            Errors = _s.PluginsError.Length > 0 ? new List<string> { DisplayText.Plain(_s.PluginsError) } : new List<string>();

            Plugins = ItemList.Sync(_plugins, _s.Plugins, delegate (PluginEntry p) { return p.Id; },
                delegate (PluginEntry p) { string id = p.Id; string name = p.Name; return new PluginCardViewModel(id, _c, delegate { ConfirmUninstall(id, name); }); },
                delegate (PluginCardViewModel card, PluginEntry p) { card.Update(p, manage); });

            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginEntry plugin in _s.Plugins) { listed.Add(plugin.Id); }
            var disk = new List<KeyValuePair<string, string>>();
            // Installed since the server started (or while it was stopped): on disk, not in the listing yet.
            if (manage)
            {
                foreach (var pair in _c.InstalledPlugins()) { if (!listed.Contains(pair.Key)) { disk.Add(pair); } }
            }
            Offline = ItemList.Sync(_offline, disk, delegate (KeyValuePair<string, string> pair) { return pair.Key; },
                delegate (KeyValuePair<string, string> pair) { string id = pair.Key; return new OfflinePluginViewModel(id, pair.Value, delegate { ConfirmUninstall(id, id); }); },
                delegate (OfflinePluginViewModel item, KeyValuePair<string, string> pair) { });

            Notice = _s.Plugins.Count == 0 && _s.PluginsError.Length == 0 && _s.PluginsLoaded ? "The server has no plugins." : string.Empty;
        }

        private void BeginInstall(string path)
        {
            PluginZipInfo info = _c.CheckPluginZip(path);
            if (info == null) { return; }
            if (!info.IsCode)
            {
                Installed(info);
                return;
            }
            string message = info.Name + " " + info.Version + (info.Author.Length > 0 ? " by " + info.Author : string.Empty)
                + " is a code plugin (" + string.Join(", ", info.Dlls.ToArray()) + "). Code plugins run inside the server with the same access as the server itself: "
                + "they can read and change every campaign, your files and the network. There's no sandbox. Install it only if you trust whoever made it as much as you'd trust any program you run.";
            _confirm("Install a code plugin?", message, "I TRUST IT, INSTALL", true, delegate { Installed(info); });
        }

        private void Installed(PluginZipInfo info)
        {
            if (!_c.InstallPlugin(info)) { return; }
            ZipPath = string.Empty;
            Refresh();
        }

        private void ConfirmUninstall(string id, string name)
        {
            _confirm("Uninstall " + name + "?",
                "Its files are deleted from the plugins folder. Campaigns keep any options it had set; content it added stops resolving after the server restarts.",
                "UNINSTALL", true, delegate { if (_c.UninstallPlugin(id)) { Refresh(); } });
        }
    }
}
