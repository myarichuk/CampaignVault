using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>Settings, tabbed: Dungeon Master, Server, Embedded, Table feel, Plugins, Advanced (which holds the tool switches).</summary>
    public sealed class SettingsOverlay : Overlay
    {
        private static readonly string[] Tabs = { "DUNGEON MASTER", "SERVER", "EMBEDDED", "TABLE FEEL", "PLUGINS", "ADVANCED" };
        private static readonly string[] TabIcons = { "skill", "server", "seal", "spark", "plugins", "settings" };
        public const int PluginsTab = 4;
        public const int AdvancedTab = 5;

        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _openSetup;
        private readonly Button[] _tabButtons = new Button[Tabs.Length];
        private VisualElement _content;
        private int _tab;
        private ProviderForm _provider;
        private string _pluginZipPath = string.Empty;

        public SettingsOverlay(VaultAppState state, VaultController controller, Action openSetup)
        {
            _state = state;
            _controller = controller;
            _openSetup = openSetup;
        }

        protected override string Title { get { return "Settings"; } }
        protected override string TitleIcon { get { return "settings"; } }

        public void ShowTab(int tab)
        {
            _tab = Mathf.Clamp(tab, 0, Tabs.Length - 1);
            if (_content != null) { Render(); }
        }

        protected override void BuildContent()
        {
            var tabs = Ui.El("cv-tabs");
            for (int i = 0; i < Tabs.Length; i++)
            {
                int index = i;
                var b = Ui.Button(Tabs[i], TabIcons[i], null, delegate { ShowTab(index); });
                b.RemoveFromClassList("cv-btn");
                b.AddToClassList("cv-tab");
                _tabButtons[i] = b;
                tabs.Add(b);
            }
            Toolbar.Add(tabs);
            _content = Ui.El("cv-settings__content");
            Body.Add(_content);
        }

        public override void OnOpen()
        {
            _state.Changed += OnChanged;
            Render();
        }

        public override void OnClose() { _state.Changed -= OnChanged; }

        private void OnChanged(StateArea area)
        {
            if (_tab == 0 && (area & StateArea.Providers) != 0 && _provider != null) { _provider.Refresh(); }
            if (_tab == 1 && (area & StateArea.Connection) != 0) { Render(); }
            if (_tab == 2 && (area & StateArea.Embedded) != 0) { Render(); }
            if (_tab == AdvancedTab && (area & StateArea.Tools) != 0) { Render(); }
            if (_tab == PluginsTab && (area & (StateArea.Plugins | StateArea.Embedded)) != 0) { Render(); }
        }

        private void Render()
        {
            for (int i = 0; i < _tabButtons.Length; i++) { _tabButtons[i].EnableInClassList("cv-tab--active", i == _tab); }
            _content.Clear();
            _provider = null;
            switch (_tab)
            {
                case 0: _provider = new ProviderForm(_content, _state, _controller, null); break;
                case 1: RenderServer(); break;
                case 2: RenderEmbedded(); break;
                case 3: RenderFeel(); break;
                case PluginsTab: RenderPlugins(); break;
                default: RenderAdvanced(); break;
            }
        }

        private void RenderServer()
        {
            var card = Ui.Card("Campaign server");
            var url = Ui.LabeledField(card, "Server URL", "http://localhost:5275", _state.Config.ServerUrl);
            var row = Ui.El("cv-row");
            var check = Ui.Button("SAVE AND CHECK", "refresh", "cv-btn--primary", delegate
            {
                _controller.SetServerUrl(url.value);
                _controller.Run(_controller.CheckConnection());
            });
            row.Add(check);
            card.Add(row);
            var status = Ui.Text(string.IsNullOrEmpty(_state.ConnectionMessage) ? "Not checked yet." : _state.ConnectionMessage, "cv-body");
            status.style.marginTop = 10;
            status.EnableInClassList("cv-text-leaf", _state.Connection == ConnectionStatus.Healthy);
            status.EnableInClassList("cv-text-blood", _state.Connection == ConnectionStatus.Down);
            status.EnableInClassList("cv-muted", _state.Connection == ConnectionStatus.Unknown || _state.Connection == ConnectionStatus.Checking);
            card.Add(status);
            _content.Add(card);

            var connector = Ui.Card("Connector");
            connector.Add(Ui.Text("Play offers the live-session tools (cheaper per turn). Build adds campaign setup tools for world building.", "cv-body cv-muted"));
            var buttons = Ui.El("cv-row");
            buttons.style.marginTop = 10;
            bool play = _state.Config.ActiveConnector() == "play";
            var playBtn = Ui.Button("PLAY", "d20", play ? "cv-btn--selected" : null, delegate { _controller.SetConnector("play"); Render(); });
            playBtn.style.marginRight = 8;
            buttons.Add(playBtn);
            buttons.Add(Ui.Button("BUILD", "map", play ? null : "cv-btn--selected", delegate { _controller.SetConnector("build"); Render(); }));
            connector.Add(buttons);
            _content.Add(connector);

            var token = Ui.Card("Bearer token");
            token.Add(Ui.Text("Only if your server requires one. Kept in memory for this run, never saved.", "cv-body cv-muted"));
            var tokenField = Ui.Password("server bearer token");
            tokenField.style.marginTop = 10;
            token.Add(tokenField);
            var tokenRow = Ui.El("cv-row");
            var set = Ui.Button("SET TOKEN", "key", null, delegate { _controller.SetBearerToken(tokenField.value); tokenField.value = string.Empty; });
            set.style.marginRight = 8;
            tokenRow.Add(set);
            tokenRow.Add(Ui.Button("CLEAR", null, "cv-btn--ghost", delegate { _controller.ClearBearerToken(); }));
            token.Add(tokenRow);
            _content.Add(token);
        }

        private void RenderEmbedded()
        {
            var server = _state.Server;
            var card = Ui.Card("Embedded server");
            card.Add(Ui.Text("Ships inside desktop builds. Runs on 127.0.0.1 only, with no token, keeping campaigns in this device's app data.", "cv-body cv-muted"));
            bool running = server != null && server.IsRunning;
            var port = Ui.LabeledField(card, "Port", "5275", server != null ? server.Port.ToString(CultureInfo.InvariantCulture) : "5275");
            port.style.maxWidth = 200;
            Ui.Switch(card, "Start automatically when the client opens", server != null && server.AutoStart, delegate (bool on) { _controller.SetEmbeddedAutostart(on); });
            var row = Ui.El("cv-row");
            row.style.marginTop = 8;
            if (running)
            {
                row.Add(Ui.Button("STOP SERVER", "stop", "cv-btn--danger", delegate { _controller.StopEmbedded(); }));
            }
            else
            {
                row.Add(Ui.Button(_state.IsBusy("embedded") ? "STARTING…" : "START SERVER", "server", "cv-btn--primary", delegate
                {
                    int p;
                    if (!int.TryParse(port.value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) { p = server != null ? server.Port : 5275; }
                    _controller.Run(_controller.StartEmbedded(p));
                }));
            }
            card.Add(row);
            string message = string.IsNullOrEmpty(_state.EmbeddedMessage) ? (running ? "Running." : "Idle.") : _state.EmbeddedMessage;
            if (running && server.ActivePort != 0 && server.ActivePort != server.Port) { message += " Port " + server.Port + " was busy, so it runs on " + server.ActivePort + "."; }
            var status = Ui.Text(message, "cv-body " + (running ? "cv-text-leaf" : "cv-muted"));
            status.style.marginTop = 10;
            card.Add(status);
            _content.Add(card);
            RenderLicense();
        }

        private void RenderLicense()
        {
            var card = Ui.Card("Database license (RavenDB)");
            card.Add(Ui.Text("Campaigns are stored in RavenDB, which asks every installation for a license key. The Community license is free and is renewed once a year: request it from RavenDB, then paste the JSON it sends you here (or the path to the file). Without one the database still runs, under RavenDB's open-source AGPL terms.", "cv-body cv-muted"));
            string holder = _controller.RavenLicenseHolder();
            var current = Ui.Text(holder == null ? "No license saved." : "License saved" + (holder.Length > 0 ? " for " + holder : string.Empty) + ". It applies from the next server start.",
                "cv-body " + (holder == null ? "cv-muted" : "cv-text-leaf"));
            current.style.marginTop = 8;
            card.Add(current);
            var input = Ui.LabeledField(card, "License JSON or file path", "{ \"Id\": \"…\", \"Name\": \"…\", \"Keys\": [ … ] }", string.Empty, true);
            input.style.height = 90;
            var row = Ui.El("cv-row");
            row.style.marginTop = 8;
            var save = Ui.Button("SAVE LICENSE", "check", "cv-btn--primary", delegate { if (_controller.SaveRavenLicense(input.value)) { Render(); } });
            save.style.marginRight = 8;
            row.Add(save);
            var request = Ui.Button("GET A FREE LICENSE", "spark", "cv-btn--ghost", delegate { Application.OpenURL(EmbeddedServerSupport.CommunityLicenseUrl); });
            request.style.marginRight = 8;
            row.Add(request);
            if (holder != null) { row.Add(Ui.Button("REMOVE", null, "cv-btn--ghost", delegate { _controller.RemoveRavenLicense(); Render(); })); }
            card.Add(row);
            _content.Add(card);
        }

        private void RenderFeel()
        {
            var card = Ui.Card("Table feel");
            Ui.Switch(card, "Animations and motion", _state.FxEnabled, delegate (bool on) { _controller.SetFx(on); });
            Ui.Switch(card, "Sound effects", !_state.SfxMuted, delegate (bool on) { _controller.SetSfxMuted(!on); VaultSfx.Muted = !on; });
            card.Add(Ui.Text("STORY TEXT SIZE", "cv-caption cv-field-caption"));
            card.Add(TextSizeControl.Build(_state, _controller));
            _content.Add(card);
        }

        /// <summary>
        /// What the Dungeon Master may do, grouped and in player terms. A tool id served on both
        /// connectors is one switch (the toggle is by name); the id and the model's description are a tooltip.
        /// </summary>
        private void RenderTools()
        {
            var card = Ui.Card("What the Dungeon Master can do");
            var top = Ui.El("cv-row");
            top.style.marginBottom = 8;
            top.Add(Ui.Text("Switch off anything you don't want the Dungeon Master to do. Switched-off abilities are never offered to the model.", "cv-body cv-muted cv-grow"));
            var all = Ui.Button("ENABLE ALL", "check", "cv-btn--small", delegate { _controller.EnableAllTools(); });
            all.style.marginLeft = 8;
            top.Add(all);
            var reload = Ui.Button("RELOAD", "refresh", "cv-btn--small cv-btn--ghost", delegate { _controller.Run(_controller.ReloadTools()); });
            reload.style.marginLeft = 8;
            top.Add(reload);
            card.Add(top);
            _content.Add(card);

            foreach (var error in _state.ToolsErrors)
            {
                card.Add(Ui.Text("Could not read the " + (error.Key == "play" ? "table" : "setup") + " abilities: " + error.Value, "cv-body cv-text-blood"));
            }
            if (_state.Tools.Count == 0)
            {
                if (_state.ToolsErrors.Count == 0)
                {
                    card.Add(Ui.Empty("plugins", _state.IsBusy("tools") ? "Asking the server what it offers…" : "Not loaded yet."));
                    if (!_state.IsBusy("tools")) { _controller.Run(_controller.LoadTools()); }
                }
                return;
            }

            var byName = new Dictionary<string, List<ToolToggle>>(StringComparer.Ordinal);
            foreach (var tool in _state.Tools)
            {
                List<ToolToggle> same;
                if (!byName.TryGetValue(tool.Name, out same)) { same = new List<ToolToggle>(); byName[tool.Name] = same; }
                same.Add(tool);
            }
            foreach (string group in ToolCatalog.Groups)
            {
                VisualElement groupBox = null;
                foreach (var pair in byName)
                {
                    var entry = ToolCatalog.Describe(pair.Key, pair.Value[0].Description);
                    if (entry.Group != group) { continue; }
                    if (groupBox == null)
                    {
                        groupBox = Ui.El("cv-toolgroup");
                        groupBox.Add(Ui.Text(group.ToUpperInvariant(), "cv-caption cv-toolgroup__title"));
                        card.Add(groupBox);
                    }
                    string toolName = pair.Key;
                    var row = Ui.El("cv-tool");
                    Ui.Switch(row, entry.Name, _state.IsToolEnabled(toolName), delegate { _controller.ToggleTool(toolName); });
                    if (entry.Blurb.Length > 0) { row.Add(Ui.Text(entry.Blurb, "cv-tool__blurb")); }
                    var where = new List<string>();
                    foreach (var t in pair.Value) { where.Add(t.Connector == "play" ? "/play" : "/build"); }
                    string raw = toolName + " (" + string.Join(", ", where.ToArray()) + ")";
                    string description = pair.Value[0].Description ?? string.Empty;
                    TooltipLayer.Attach(row, raw + (description.Length > 0 ? "\n" + (description.Length > 320 ? description.Substring(0, 320) + "…" : description) : string.Empty));
                    groupBox.Add(row);
                }
            }
        }

        private void RenderPlugins()
        {
            bool manage = _controller.CanManagePlugins;
            var server = _state.Server;
            bool running = server != null && server.IsRunning;

            if (manage && _state.PluginsRestartNeeded)
            {
                var banner = Ui.Card(_state.IsBusy("embedded") ? "Restarting the server" : "Restart coming");
                banner.AddToClassList("cv-card--warning");
                banner.Add(Ui.Text("The server restarts by itself in a moment to apply plugin changes (after the current turn, if one is running). The list below is what it loaded at its last start.", "cv-body"));
                var restartRow = Ui.El("cv-row");
                restartRow.style.marginTop = 10;
                restartRow.Add(Ui.Button(_state.IsBusy("embedded") ? "RESTARTING…" : "RESTART NOW", "refresh", "cv-btn--primary", delegate { _controller.Run(_controller.RestartEmbedded()); }));
                banner.Add(restartRow);
                _content.Add(banner);
            }

            if (manage)
            {
                var install = Ui.Card("Install a plugin");
                install.Add(Ui.Text("Plugins add rules content (spells, items, creatures) or whole new mechanics. Put the plugin's .zip somewhere on this computer and enter its path. Installed plugins live in your app data and survive client updates.", "cv-body cv-muted"));
                var path = Ui.LabeledField(install, "Path to the .zip", "/Users/you/Downloads/my-plugin.zip", _pluginZipPath);
                path.RegisterValueChangedCallback(delegate (ChangeEvent<string> e) { _pluginZipPath = e.newValue; });
                var row = Ui.El("cv-row");
                row.style.marginTop = 8;
                var go = Ui.Button("INSTALL", "add", "cv-btn--primary", delegate { BeginInstall(path.value); });
                go.style.marginRight = 8;
                row.Add(go);
                row.Add(Ui.Button("OPEN PLUGINS FOLDER", "pack", "cv-btn--ghost", delegate { _controller.OpenPluginsFolder(); }));
                install.Add(row);
                _content.Add(install);
            }

            var top = Ui.El("cv-row");
            top.style.marginBottom = 12;
            top.style.marginTop = 4;
            string intro = !manage ? "Plugins on the server you're connected to. Manage them on the machine that runs it."
                : running ? "Plugins the built-in server found at its last start."
                : "The built-in server isn't running: plugins installed here load when it starts.";
            top.Add(Ui.Text(intro, "cv-body cv-muted cv-grow"));
            var reload = Ui.Button("RELOAD", "refresh", "cv-btn--small cv-btn--ghost", delegate { _controller.Run(_controller.LoadPlugins()); });
            reload.style.marginLeft = 8;
            top.Add(reload);
            _content.Add(top);

            if (!_state.PluginsLoaded && !_state.IsBusy("plugins")) { _controller.Run(_controller.LoadPlugins()); }
            if (_state.PluginsError.Length > 0) { _content.Add(Ui.Text(_state.PluginsError, "cv-body cv-text-blood")); }

            var listed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginEntry plugin in _state.Plugins)
            {
                listed.Add(plugin.Id);
                _content.Add(PluginCard(plugin, manage));
            }

            // Installed since the server started (or while it was stopped): on disk, not in the listing yet.
            if (manage)
            {
                foreach (var pair in _controller.InstalledPlugins())
                {
                    if (listed.Contains(pair.Key)) { continue; }
                    var card = Ui.Card(pair.Key);
                    card.Add(Ui.Text("Installed in " + pair.Value + ". The server loads it on its next start.", "cv-body cv-muted"));
                    string id = pair.Key;
                    var row = Ui.El("cv-row");
                    row.style.marginTop = 8;
                    row.Add(Ui.Button("UNINSTALL", "trash", "cv-btn--ghost cv-btn--small", delegate { ConfirmUninstall(id, id); }));
                    card.Add(row);
                    _content.Add(card);
                }
            }

            if (_state.Plugins.Count == 0 && _state.PluginsError.Length == 0 && _state.PluginsLoaded)
            {
                _content.Add(Ui.Empty("plugins", "The server has no plugins."));
            }
        }

        private VisualElement PluginCard(PluginEntry plugin, bool manage)
        {
            var card = Ui.Card(plugin.Name + (plugin.Version.Length > 0 ? "  ·  v" + plugin.Version : string.Empty));
            var meta = new System.Collections.Generic.List<string>();
            if (plugin.Author.Length > 0) { meta.Add("by " + plugin.Author); }
            meta.Add(plugin.IsCode ? "Code plugin" : "Data only");
            meta.Add(plugin.IsUser ? "Installed by you" : "Bundled");
            if (plugin.Systems.Count > 0) { meta.Add("for " + string.Join(", ", plugin.Systems.ToArray())); }
            card.Add(Ui.Text(string.Join("  ·  ", meta.ToArray()), "cv-caption"));
            if (plugin.Description.Length > 0) { card.Add(Ui.Text(plugin.Description, "cv-body")); }
            var adds = new System.Collections.Generic.List<string>();
            if (plugin.ModeIds.Count > 0) { adds.Add("modes: " + string.Join(", ", plugin.ModeIds.ToArray())); }
            if (plugin.OptionKeys.Count > 0) { adds.Add("campaign options: " + string.Join(", ", plugin.OptionKeys.ToArray())); }
            if (adds.Count > 0) { card.Add(Ui.Text("Adds " + string.Join("; ", adds.ToArray()), "cv-body cv-muted")); }
            foreach (string error in plugin.Errors) { card.Add(Ui.Text(error, "cv-body cv-text-blood")); }

            string state = !plugin.Enabled ? "Disabled." : plugin.Loaded ? "Loaded." : "Not loaded.";
            var status = Ui.Text(state, "cv-body " + (plugin.Loaded ? "cv-text-leaf" : "cv-muted"));
            status.style.marginTop = 6;
            card.Add(status);

            if (manage)
            {
                string id = plugin.Id;
                string name = plugin.Name;
                // The switch shows what the server will load; flipping it restarts the server shortly.
                Ui.Switch(card, "Enabled", !_controller.IsPluginDisabled(id), delegate (bool on) { _controller.SetPluginEnabled(id, on); });
                if (plugin.IsUser)
                {
                    var row = Ui.El("cv-row");
                    row.Add(Ui.Button("UNINSTALL", "trash", "cv-btn--ghost cv-btn--small", delegate { ConfirmUninstall(id, name); }));
                    card.Add(row);
                }
            }
            return card;
        }

        private void BeginInstall(string path)
        {
            PluginZipInfo info = _controller.CheckPluginZip(path);
            if (info == null) { return; }
            if (!info.IsCode)
            {
                if (_controller.InstallPlugin(info)) { _pluginZipPath = string.Empty; Render(); }
                return;
            }
            string message = info.Name + " " + info.Version + (info.Author.Length > 0 ? " by " + info.Author : string.Empty)
                + " is a code plugin (" + string.Join(", ", info.Dlls.ToArray()) + "). Code plugins run inside the server with the same access as the server itself: "
                + "they can read and change every campaign, your files and the network. There's no sandbox. Install it only if you trust whoever made it as much as you'd trust any program you run.";
            Host.Open(new ConfirmOverlay("Install a code plugin?", message, "I TRUST IT, INSTALL", true, delegate
            {
                if (_controller.InstallPlugin(info)) { _pluginZipPath = string.Empty; Render(); }
            }));
        }

        private void ConfirmUninstall(string id, string name)
        {
            Host.Open(new ConfirmOverlay("Uninstall " + name + "?",
                "Its files are deleted from the plugins folder. Campaigns keep any options it had set; content it added stops resolving after the server restarts.",
                "UNINSTALL", true, delegate { if (_controller.UninstallPlugin(id)) { Render(); } }));
        }

        private void RenderAdvanced()
        {
            var skills = Ui.Card("Dungeon Master prompt");
            int count;
            string names;
            bool ok = _state.Prompts.TryGetSkillStatus(out count, out names) && count > 0;
            skills.Add(Ui.Text(ok ? "system-prompt.md plus " + count + " skills: " + names : "The prompt or skills aren't staged. Re-run the StreamingAssets copy in the client README.", "cv-body " + (ok ? "cv-text-leaf" : "cv-text-blood")));
            _content.Add(skills);

            RenderTools();

            var tools = Ui.Card("Troubleshooting");
            var row = Ui.El("cv-row");
            var setup = Ui.Button("RUN FIRST-TIME SETUP", "spark", null, delegate { Close(); if (_openSetup != null) { _openSetup(); } });
            setup.style.marginRight = 8;
            row.Add(setup);
            tools.Add(row);
            tools.Add(Ui.Text("Press F12 at the table for the inspector: the last request, the raw model reply and the tool log.", "cv-body cv-muted"));
            _content.Add(tools);

            var data = Ui.Card("Data on this device");
            data.Add(Ui.KeyValue("App data", Application.persistentDataPath));
            _content.Add(data);
        }
    }

    /// <summary>First run: server, Dungeon Master, then your first campaign. Can't be dismissed until a provider works.</summary>
    public sealed class SetupOverlay : Overlay
    {
        private static readonly string[] Steps = { "The server", "Your Dungeon Master", "Your table" };

        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _openOnboarding;
        private readonly Action _openCampaigns;
        private VisualElement _content;
        private Label _stepTitle;
        private Button _back;
        private Button _next;
        private int _step;
        private ProviderForm _provider;

        public SetupOverlay(VaultAppState state, VaultController controller, Action openOnboarding, Action openCampaigns)
        {
            _state = state;
            _controller = controller;
            _openOnboarding = openOnboarding;
            _openCampaigns = openCampaigns;
        }

        protected override string Title { get { return "Welcome to the Vault"; } }
        protected override string TitleIcon { get { return "crest"; } }
        protected override bool Narrow { get { return true; } }

        public override bool CanDismiss
        {
            get
            {
                string reason;
                return _state.Byok.Validate(out reason);
            }
        }

        protected override void BuildContent()
        {
            _stepTitle = Ui.Text(string.Empty, "cv-caption");
            _stepTitle.style.marginBottom = 12;
            Body.Add(_stepTitle);
            _content = Ui.El();
            Body.Add(_content);
            _back = Ui.Button("BACK", null, "cv-btn--ghost", delegate { Go(_step - 1); });
            Foot.Add(_back);
            _next = Ui.Button("NEXT", "chevron", "cv-btn--primary", Advance);
            Foot.Add(_next);
        }

        public override void OnOpen()
        {
            _state.Changed += OnChanged;
            Go(0);
        }

        public override void OnClose() { _state.Changed -= OnChanged; }

        private void OnChanged(StateArea area)
        {
            if ((area & StateArea.Providers) != 0 && _provider != null) { _provider.Refresh(); PaintNav(); }
            if ((area & StateArea.Connection) != 0 && _step == 0) { Render(); }
        }

        private void Go(int step)
        {
            _step = Mathf.Clamp(step, 0, Steps.Length - 1);
            Render();
        }

        private void Advance()
        {
            if (_step < Steps.Length - 1) { Go(_step + 1); return; }
            Close();
        }

        private void PaintNav()
        {
            string reason;
            bool ready = _state.Byok.Validate(out reason);
            _back.style.display = _step > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _next.SetEnabled(_step != 1 || ready);
            Ui.SetButtonText(_next, _step == Steps.Length - 1 ? "TO THE TABLE" : "NEXT");
        }

        private void Render()
        {
            Ui.SetText(_stepTitle, "STEP " + (_step + 1) + " OF " + Steps.Length + " · " + Steps[_step].ToUpperInvariant());
            _content.Clear();
            _provider = null;
            if (_step == 0)
            {
                _content.Add(Ui.Text("Your game is run by an AI Dungeon Master that plays through the CampaignVault server, which keeps every campaign safe. Desktop builds start a server on this machine automatically; if you host one elsewhere, enter its address.", "cv-body"));
                var url = Ui.LabeledField(_content, "Server URL", "http://localhost:5275", _state.Config.ServerUrl);
                _content.Add(Ui.Button("SAVE AND CHECK", "refresh", null, delegate { _controller.SetServerUrl(url.value); _controller.Run(_controller.CheckConnection()); }));
                string message = _state.Connection == ConnectionStatus.Healthy ? "The server is healthy."
                    : _state.Connection == ConnectionStatus.Down ? "Not reachable yet (" + _state.ConnectionMessage + "). The embedded server can take a few seconds to start: check again, or continue and fix it later in Settings."
                    : _state.Connection == ConnectionStatus.Checking ? "Checking…" : string.Empty;
                var status = Ui.Text(message, "cv-body " + (_state.Connection == ConnectionStatus.Healthy ? "cv-text-leaf" : "cv-muted"));
                status.style.marginTop = 10;
                _content.Add(status);
                if (_state.Connection == ConnectionStatus.Unknown) { _controller.Run(_controller.CheckConnection()); }
                if (_state.Server != null && _controller.RavenLicenseHolder() == null)
                {
                    var license = Ui.Text("The built-in server stores campaigns in RavenDB, which asks for a license key. The Community key is free: add it any time under Settings → Embedded.", "cv-body cv-muted");
                    license.style.marginTop = 10;
                    _content.Add(license);
                }
            }
            else if (_step == 1)
            {
                _content.Add(Ui.Text("Connect the model that will be your Dungeon Master. Pick a preset, paste your key and test it. You can keep several profiles and switch any time: campaigns don't belong to a model.", "cv-body"));
                var form = Ui.El();
                form.style.marginTop = 14;
                _content.Add(form);
                _provider = new ProviderForm(form, _state, _controller, PaintNav);
            }
            else
            {
                _content.Add(Ui.Text("You're connected. A short guided questionnaire (name, rules, tone, party) creates your campaign and hands it to the Dungeon Master.", "cv-body"));
                var row = Ui.El("cv-row");
                row.style.marginTop = 16;
                var create = Ui.Button("CREATE A CAMPAIGN", "add", "cv-btn--primary", delegate { Close(); if (_openOnboarding != null) { _openOnboarding(); } });
                create.style.marginRight = 8;
                row.Add(create);
                row.Add(Ui.Button("OPEN MY CAMPAIGNS", "campaigns", null, delegate { Close(); if (_openCampaigns != null) { _openCampaigns(); } }));
                _content.Add(row);
            }
            PaintNav();
        }
    }

    /// <summary>F12: what the driver last sent and got back, token usage, and its tool log. Everything is selectable.</summary>
    public sealed class InspectorOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private TextField _request;
        private TextField _usage;
        private TextField _response;
        private TextField _tools;

        public InspectorOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "Inspector"; } }
        protected override string TitleIcon { get { return "inspect"; } }

        protected override void BuildContent()
        {
            _request = Area("LAST REQUEST", 60);
            _usage = Area("TOKENS (PROVIDER-REPORTED)", 120);
            _response = Area("LAST MODEL RESPONSE (RAW JSON)", 280);
            _tools = Area("TOOL CALLS", 140);
            Foot.Add(Ui.Button("COPY RESPONSE", null, null, delegate { GUIUtility.systemCopyBuffer = _state.Driver.LastResponseJson; _state.RaiseToast("Response copied.", ToastKind.Success); }));
            Foot.Add(Ui.Button("COPY TOOL LOG", null, null, delegate { GUIUtility.systemCopyBuffer = string.Join("\n", _state.Driver.ToolLog.ToArray()); _state.RaiseToast("Tool log copied.", ToastKind.Success); }));
            Foot.Add(Ui.Button("COPY SYSTEM PROMPT", null, null, delegate { GUIUtility.systemCopyBuffer = _state.Prompts.BuildSystemPrompt(); _state.RaiseToast("System prompt copied.", ToastKind.Success); }));
            Foot.Add(Ui.Button("EXPORT TRANSCRIPT", null, null, ExportTranscript));
        }

        private void ExportTranscript()
        {
            string path = _controller.ExportTranscript();
            if (path == null) { return; }
            GUIUtility.systemCopyBuffer = path;
            _state.RaiseToast("Transcript saved (path copied): " + path, ToastKind.Success);
        }

        private TextField Area(string caption, int height)
        {
            Body.Add(Ui.Text(caption, "cv-caption"));
            var f = Ui.Field(string.Empty, string.Empty, true);
            f.isReadOnly = true;
            f.AddToClassList("cv-mono");
            f.style.height = height;
            Body.Add(f);
            return f;
        }

        public override void OnOpen()
        {
            _state.Changed += OnChanged;
            Paint();
        }

        public override void OnClose() { _state.Changed -= OnChanged; }

        private void OnChanged(StateArea area) { if ((area & StateArea.Driver) != 0) { Paint(); } }

        private void Paint()
        {
            var d = _state.Driver;
            _request.value = string.IsNullOrEmpty(d.LastRequestMeta) ? "(no request sent yet)" : d.LastRequestMeta;
            _usage.value = DescribeUsage(d);
            _response.value = string.IsNullOrEmpty(d.LastResponseJson) ? "(no response yet)" : d.LastResponseJson;
            _tools.value = d.ToolLog.Count == 0 ? "(none yet)" : string.Join("\n", d.ToolLog.ToArray());
        }

        internal static string DescribeUsage(OpenAiChatDriver d)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Session: ").Append(d.SessionUsage).Append(" · ").Append(d.Turns.Count).Append(" turns");
            if (d.Turns.Count > 0 && !d.SessionUsage.IsEmpty)
            {
                sb.Append(" · avg ").Append(d.SessionUsage.Prompt / d.Turns.Count).Append(" in / ")
                  .Append(d.SessionUsage.Completion / d.Turns.Count).Append(" out per turn");
            }
            int from = Math.Max(0, d.Turns.Count - 5);
            for (int i = d.Turns.Count - 1; i >= from; i--)
            {
                var t = d.Turns[i];
                sb.Append("\nTurn ").Append(i + 1).Append(": ").Append(t.Usage)
                  .Append(" · ").Append(t.Tools.Count).Append(" tools · ")
                  .Append(t.Narration.Length).Append(" chars of narration");
            }
            return sb.ToString();
        }
    }

    /// <summary>A yes/no question that doesn't block the thread (no native dialogs).</summary>
    public sealed class ConfirmOverlay : Overlay
    {
        private readonly string _title;
        private readonly string _message;
        private readonly string _confirm;
        private readonly bool _danger;
        private readonly Action _onConfirm;

        public ConfirmOverlay(string title, string message, string confirm, bool danger, Action onConfirm)
        {
            _title = title;
            _message = message;
            _confirm = confirm;
            _danger = danger;
            _onConfirm = onConfirm;
        }

        protected override string Title { get { return _title; } }
        protected override bool Narrow { get { return true; } }

        protected override void BuildContent()
        {
            Body.Add(Ui.Text(_message, "cv-body"));
            Foot.Add(Ui.Button("CANCEL", null, "cv-btn--ghost", Close));
            Foot.Add(Ui.Button(_confirm, null, _danger ? "cv-btn--danger" : "cv-btn--primary", delegate { Close(); _onConfirm(); }));
        }
    }
}
