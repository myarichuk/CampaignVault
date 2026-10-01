using System;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Party frames down the left edge: layout in Templates/Table/Party.uxml, state in <see cref="PartyViewModel"/>.
    /// Frames are kept per character id, so HP changes animate instead of redrawing.
    /// </summary>
    public sealed class PartyFramesView
    {
        public PartyFramesView(VisualElement host, VaultAppState state, VaultController controller, Action<string> openSheet, Action openCampaigns, Action openBuilder)
        {
            ViewModel = new PartyViewModel(state, controller, openSheet, openCampaigns, openBuilder);
            Templates.CloneInto("Table/Party", host);
            ViewModel.Refresh();
            host.dataSource = ViewModel;
        }

        public PartyViewModel ViewModel { get; private set; }
    }

    /// <summary>
    /// The codex drawer (Quests, Scene, Pack, Journal): layout in Templates/Table/Codex.uxml and a page template per
    /// tab, state in <see cref="CodexViewModel"/>.
    /// </summary>
    public sealed class CodexView
    {
        public CodexView(VisualElement host, VaultAppState state, VaultController controller, Action<string> openSheet)
        {
            ViewModel = new CodexViewModel(state, controller, openSheet);
            Templates.CloneInto("Table/Codex", host);
            ViewModel.Show(0);
            host.dataSource = ViewModel;
        }

        public CodexViewModel ViewModel { get; private set; }

        public void Show(int tab) { ViewModel.Show(tab); }

        /// <summary>See <see cref="ScenePage.DescribeActivity"/>.</summary>
        internal static string DescribeActivity(string name, string activity) { return ScenePage.DescribeActivity(name, activity); }
    }
}
