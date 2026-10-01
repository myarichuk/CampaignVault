using System;
using System.Collections.Generic;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>One thing the Dungeon Master may do, as a switch. A tool served on both connectors is one switch (the toggle is by name).</summary>
    public sealed class ToolRowViewModel : ViewModel, IKeyed
    {
        private readonly VaultController _c;
        private bool _enabled;

        public ToolRowViewModel(string tool, VaultController controller)
        {
            Key = tool;
            Name = "tool-" + tool;
            _c = controller;
        }

        private string _title = string.Empty;
        private string _blurb = string.Empty;
        private string _tooltip = string.Empty;

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Title { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string Blurb { get { return _blurb; } private set { Set(ref _blurb, value); } }
        /// <summary>The tool's id and the model's description, on hover.</summary>
        [CreateProperty] public string Tooltip { get { return _tooltip; } private set { Set(ref _tooltip, value); } }

        [CreateProperty]
        public bool Enabled
        {
            get { return _enabled; }
            set { if (Set(ref _enabled, value)) { _c.ToggleTool(Key); } }
        }

        public void Update(List<ToolToggle> same, bool enabled)
        {
            Set(ref _enabled, enabled, "Enabled");
            string description = same[0].Description ?? string.Empty;
            var entry = ToolCatalog.Describe(Key, description);
            var where = new List<string>();
            foreach (var t in same) { where.Add(t.Connector == "play" ? "/play" : "/build"); }
            Title = DisplayText.Plain(entry.Name);
            Blurb = DisplayText.Plain(entry.Blurb);
            Tooltip = Key + " (" + string.Join(", ", where.ToArray()) + ")"
                + (description.Length > 0 ? "\n" + (description.Length > 320 ? description.Substring(0, 320) + "…" : description) : string.Empty);
        }
    }

    public sealed class ToolGroupViewModel : ViewModel, IKeyed
    {
        public ToolGroupViewModel(string group)
        {
            Key = group;
            Title = group.ToUpperInvariant();
            _tools = new List<ToolRowViewModel>();
        }

        private List<ToolRowViewModel> _tools;

        public string Key { get; private set; }
        [CreateProperty] public string Title { get; private set; }
        [CreateProperty] public List<ToolRowViewModel> Tools { get { return _tools; } set { SetList(ref _tools, value); } }
    }

    /// <summary>The Dungeon Master prompt, what the Dungeon Master may do, first-time setup and where the data lives.</summary>
    public sealed class AdvancedPage : SettingsPage
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private List<ToolGroupViewModel> _groups = new List<ToolGroupViewModel>();
        private List<string> _errors = new List<string>();
        private string _notice = string.Empty;
        private string _prompt = string.Empty;
        private string _promptTone = "muted";

        public AdvancedPage(VaultAppState state, VaultController controller, Action closeAndSetup)
        {
            _s = state;
            _c = controller;
            EnableAll = delegate { controller.EnableAllTools(); };
            ReloadTools = delegate { controller.Run(controller.ReloadTools()); };
            RunSetup = closeAndSetup;
            AppData = DisplayText.Plain(Application.persistentDataPath);
            Watch(state, StateArea.Tools);
            Refresh();
        }

        public override string Template { get { return "Settings/AdvancedPage"; } }

        [CreateProperty] public string Prompt { get { return _prompt; } private set { Set(ref _prompt, value); } }
        [CreateProperty] public string PromptTone { get { return _promptTone; } private set { Set(ref _promptTone, value); } }
        [CreateProperty] public List<ToolGroupViewModel> Groups { get { return _groups; } private set { SetList(ref _groups, value); } }
        /// <summary>Which connector's abilities couldn't be read, and why.</summary>
        [CreateProperty] public List<string> Errors { get { return _errors; } private set { SetList(ref _errors, value); } }
        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public string AppData { get; private set; }
        [CreateProperty] public Action EnableAll { get; private set; }
        [CreateProperty] public Action ReloadTools { get; private set; }
        [CreateProperty] public Action RunSetup { get; private set; }

        public override void Enter()
        {
            if (_s.Tools.Count == 0 && _s.ToolsErrors.Count == 0 && !_s.IsBusy("tools")) { _c.Run(_c.LoadTools()); }
        }

        public override void Refresh()
        {
            int count;
            string names;
            bool ok = _s.Prompts.TryGetSkillStatus(out count, out names) && count > 0;
            Prompt = DisplayText.Plain(ok ? "system-prompt.md plus " + count + " skills: " + names : "The prompt or skills aren't staged. Re-run the StreamingAssets copy in the client README.");
            PromptTone = ok ? "leaf" : "blood";

            var errors = new List<string>();
            foreach (var error in _s.ToolsErrors)
            {
                errors.Add(DisplayText.Plain("Could not read the " + (error.Key == "play" ? "table" : "setup") + " abilities: " + error.Value));
            }
            Errors = errors;
            Notice = _s.Tools.Count == 0 && _s.ToolsErrors.Count == 0
                ? (_s.IsBusy("tools") ? "Asking the server what it offers…" : "Not loaded yet.") : string.Empty;

            var byName = new Dictionary<string, List<ToolToggle>>(StringComparer.Ordinal);
            foreach (var tool in _s.Tools)
            {
                List<ToolToggle> same;
                if (!byName.TryGetValue(tool.Name, out same)) { same = new List<ToolToggle>(); byName[tool.Name] = same; }
                same.Add(tool);
            }
            var inGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in byName)
            {
                string group = ToolCatalog.Describe(pair.Key, pair.Value[0].Description).Group;
                List<string> names2;
                if (!inGroup.TryGetValue(group, out names2)) { names2 = new List<string>(); inGroup[group] = names2; }
                names2.Add(pair.Key);
            }
            var present = new List<string>();
            foreach (string group in ToolCatalog.Groups) { if (inGroup.ContainsKey(group)) { present.Add(group); } }
            Groups = ItemList.Sync(_groups, present, delegate (string g) { return g; },
                delegate (string g) { return new ToolGroupViewModel(g); },
                delegate (ToolGroupViewModel vm, string g)
                {
                    vm.Tools = ItemList.Sync(vm.Tools, inGroup[g], delegate (string t) { return t; },
                        delegate (string t) { return new ToolRowViewModel(t, _c); },
                        delegate (ToolRowViewModel row, string t) { row.Update(byName[t], _s.IsToolEnabled(t)); });
                });
        }
    }
}
