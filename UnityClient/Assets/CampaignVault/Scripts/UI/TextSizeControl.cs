using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The story text size: an A− / size / A+ stepper (used by the top bar's
    /// "Aa" menu and by Settings), and the root class that applies it. The
    /// sizes themselves live in USS (.cv-story-N in table.uss).
    /// </summary>
    public static class TextSizeControl
    {
        private static readonly string[] Names = { "Smallest", "Small", "Default", "Large", "Larger", "Largest" };

        public static string NameOf(int step)
        {
            return step >= 0 && step < Names.Length ? Names[step] : "Default";
        }

        /// <summary>Puts exactly one cv-story-N class on the root.</summary>
        public static void Apply(VisualElement root, int step)
        {
            for (int i = 0; i < VaultAppState.StoryTextSizes.Length; i++) { root.EnableInClassList("cv-story-" + i, i == step); }
        }

        /// <summary>A stepper that keeps itself in step with the preference, wherever it was changed.</summary>
        public static VisualElement Build(VaultAppState state, VaultController controller)
        {
            var row = Ui.El("cv-textsize");
            Label value = null;
            Button smaller = null;
            Button larger = null;
            System.Action paint = delegate
            {
                int step = state.StoryTextSize;
                Ui.SetText(value, NameOf(step) + " · " + VaultAppState.StoryTextSizes[step] + " px");
                smaller.SetEnabled(step > 0);
                larger.SetEnabled(step < VaultAppState.StoryTextSizes.Length - 1);
            };
            smaller = Ui.Button("A−", null, "cv-btn--small cv-textsize__step", delegate { controller.SetStoryTextSize(state.StoryTextSize - 1); });
            TooltipLayer.Attach(smaller, "Smaller story text (Ctrl/Cmd −)");
            value = Ui.Text(string.Empty, "cv-textsize__value");
            larger = Ui.Button("A+", null, "cv-btn--small cv-textsize__step cv-textsize__step--up", delegate { controller.SetStoryTextSize(state.StoryTextSize + 1); });
            TooltipLayer.Attach(larger, "Larger story text (Ctrl/Cmd +)");
            row.Add(smaller);
            row.Add(value);
            row.Add(larger);
            paint();
            System.Action<StateArea> onChanged = delegate (StateArea area) { if ((area & StateArea.Preferences) != 0) { paint(); } };
            // Settings rebuilds its pages: unhook when this stepper leaves the panel.
            row.RegisterCallback<AttachToPanelEvent>(delegate { state.Changed -= onChanged; state.Changed += onChanged; paint(); });
            row.RegisterCallback<DetachFromPanelEvent>(delegate { state.Changed -= onChanged; });
            return row;
        }
    }
}
