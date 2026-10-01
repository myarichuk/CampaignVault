using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The story text size's names and the root class that applies it. The stepper itself is
    /// <see cref="Settings.TextSizeViewModel"/> (top bar menu and Settings); the sizes live in USS (.cv-story-N in table.uss).
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
    }
}
