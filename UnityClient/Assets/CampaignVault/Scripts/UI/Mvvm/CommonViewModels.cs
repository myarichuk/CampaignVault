using System;
using Unity.Properties;

namespace CampaignVault.UnityClient.UI.Mvvm
{
    /// <summary>Asks the player a yes/no question (the page's overlay opens a ConfirmOverlay).</summary>
    public delegate void ConfirmDialog(string title, string message, string confirm, bool danger, Action onConfirm);

    /// <summary>Runs an action after a delay (the page's overlay schedules it; tests run it by hand).</summary>
    public delegate void Later(Action action, long milliseconds);

    /// <summary>
    /// One choice among several (a profile, a preset, a rules system): a button that shows whether it is the current
    /// one. Shown with Templates/Common/Choice.uxml.
    /// </summary>
    public sealed class ChoiceViewModel : ViewModel, IKeyed
    {
        private string _label;
        private string _icon;
        private bool _selected;

        public ChoiceViewModel(string key, string label, string icon, bool small, Action pick, string name = "")
        {
            Key = key;
            _label = label;
            _icon = icon ?? string.Empty;
            Small = small;
            Pick = pick;
            Name = name;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public bool Small { get; private set; }
        [CreateProperty] public Action Pick { get; private set; }
        [CreateProperty] public string Label { get { return _label; } private set { Set(ref _label, value); } }
        [CreateProperty] public string Icon { get { return _icon; } private set { Set(ref _icon, value ?? string.Empty); } }
        [CreateProperty] public bool Selected { get { return _selected; } private set { Set(ref _selected, value); } }

        public void Update(string label, string icon, bool selected)
        {
            Label = label;
            Icon = icon;
            Selected = selected;
        }
    }

    /// <summary>A tab in a row of tabs (Templates/Common/Tab.uxml); the open one is <see cref="Active"/>.</summary>
    public sealed class TabViewModel : ViewModel, IKeyed
    {
        private bool _active;

        public TabViewModel(string key, string name, string label, string icon, Action select)
        {
            Key = key;
            Name = name;
            Label = label.ToUpperInvariant();
            Icon = icon;
            Select = select;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
        [CreateProperty] public bool Active { get { return _active; } set { Set(ref _active, value); } }
        [CreateProperty] public Action Select { get; private set; }
    }

    /// <summary>A button in a page's foot or toolbar, as data: what it says, whether it can be pressed and what it does.</summary>
    public sealed class ActionViewModel : ViewModel, IKeyed
    {
        private string _label = string.Empty;
        private string _icon = string.Empty;
        private bool _enabled = true;
        private string _tooltip = string.Empty;

        public ActionViewModel(string key, string name, Action run, bool primary = false, bool ghost = false, bool danger = false)
        {
            Key = key;
            Name = name;
            Run = run;
            Primary = primary;
            Ghost = ghost;
            Danger = danger;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public Action Run { get; private set; }
        [CreateProperty] public bool Primary { get; private set; }
        [CreateProperty] public bool Ghost { get; private set; }
        [CreateProperty] public bool Danger { get; private set; }
        [CreateProperty] public string Label { get { return _label; } private set { Set(ref _label, value ?? string.Empty); } }
        [CreateProperty] public string Icon { get { return _icon; } private set { Set(ref _icon, value ?? string.Empty); } }
        [CreateProperty] public bool Enabled { get { return _enabled; } private set { Set(ref _enabled, value); } }
        [CreateProperty] public string Tooltip { get { return _tooltip; } private set { Set(ref _tooltip, value ?? string.Empty); } }

        public ActionViewModel Show(string label, string icon = null, bool enabled = true, string tooltip = null)
        {
            Label = label;
            Icon = icon;
            Enabled = enabled;
            Tooltip = tooltip;
            return this;
        }
    }
}
