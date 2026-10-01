using System;
using System.Collections.Generic;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>A page of Settings. It is made once per visit to Settings, so typing on one tab survives a trip to another.</summary>
    public abstract class SettingsPage : ViewModel, ITemplated
    {
        public abstract string Template { get; }

        /// <summary>The tab was opened: ask the server for what the page lists, if it hasn't been asked.</summary>
        public virtual void Enter() { }
    }

    /// <summary>
    /// Settings, tabbed (Templates/Settings/Settings.uxml): Dungeon Master, Server, Embedded, Table feel, Plugins and
    /// Advanced (which holds the tool switches). Each tab is a page view model shown with its own template.
    /// </summary>
    public sealed class SettingsViewModel : ViewModel
    {
        public const int ProviderTab = 0;
        public const int PluginsTab = 4;
        public const int AdvancedTab = 5;

        private static readonly string[] Names = { "DUNGEON MASTER", "SERVER", "EMBEDDED", "TABLE FEEL", "PLUGINS", "ADVANCED" };
        private static readonly string[] Icons = { "skill", "server", "seal", "spark", "plugins", "settings" };

        private readonly ViewModel[] _pages;
        private ViewModel _page;
        private int _tab = -1;

        public SettingsViewModel(VaultAppState state, VaultController controller, ConfirmDialog confirm, Action closeAndSetup)
        {
            _pages = new ViewModel[]
            {
                new ProviderFormViewModel(state, controller),
                new ServerPage(state, controller),
                new EmbeddedPage(state, controller),
                new FeelPage(state, controller),
                new PluginsPage(state, controller, confirm),
                new AdvancedPage(state, controller, closeAndSetup),
            };
            Tabs = new List<TabViewModel>();
            for (int i = 0; i < Names.Length; i++)
            {
                int index = i;
                Tabs.Add(new TabViewModel(i.ToString(), "settings-tab-" + i, Names[i], Icons[i], delegate { Show(index); }));
            }
        }

        [CreateProperty] public List<TabViewModel> Tabs { get; private set; }
        [CreateProperty] public ViewModel Page { get { return _page; } private set { Set(ref _page, value); } }

        public int Tab { get { return _tab; } }

        public void Show(int tab)
        {
            tab = Mathf.Clamp(tab, 0, _pages.Length - 1);
            bool entering = tab != _tab;
            _tab = tab;
            for (int i = 0; i < Tabs.Count; i++) { Tabs[i].Active = i == tab; }
            Page = _pages[tab];
            var page = _pages[tab] as SettingsPage;
            if (entering && page != null) { page.Enter(); }
        }

        public override void Dispose()
        {
            foreach (var page in _pages) { page.Dispose(); }
            base.Dispose();
        }
    }
}
