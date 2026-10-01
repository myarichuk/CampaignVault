using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI.Mvvm
{
    /// <summary>
    /// UXML layouts under Resources/VaultUI/Templates (the HTML of the client; Theme/*.uss is its CSS). Templates
    /// carry no &lt;Style&gt; of their own: the theme styles everything, so a cloned element looks the same wherever
    /// it lands.
    /// </summary>
    public static class Templates
    {
        public const string ResourceRoot = "VaultUI/Templates/";
        private static readonly Dictionary<string, VisualTreeAsset> Cache = new Dictionary<string, VisualTreeAsset>();

        public static VisualTreeAsset Load(string path)
        {
            VisualTreeAsset asset;
            if (Cache.TryGetValue(path, out asset) && asset != null) { return asset; }
            asset = Resources.Load<VisualTreeAsset>(ResourceRoot + path);
            if (asset == null) { throw new KeyNotFoundException("No UXML template at Resources/" + ResourceRoot + path + ".uxml"); }
            Cache[path] = asset;
            return asset;
        }

        /// <summary>The template's single root element, without the TemplateContainer wrapper (which would break flex rows).</summary>
        public static VisualElement Clone(string path)
        {
            var container = Load(path).Instantiate();
            if (container.childCount != 1) { return container; }
            var root = container[0];
            root.RemoveFromHierarchy();
            return root;
        }

        /// <summary>Every top-level element of the template, added to parent.</summary>
        public static void CloneInto(string path, VisualElement parent)
        {
            Load(path).CloneTree(parent);
        }
    }

    /// <summary>Text a view model hands to a label: what the element builders did when views built labels.</summary>
    public static class DisplayText
    {
        /// <summary>Plain text: rich-text tags in it are shown, never interpreted.</summary>
        public static string Plain(string text)
        {
            return MarkdownLite.Escape(TextSanitizer.Clean(text ?? string.Empty));
        }

        /// <summary>Model or server prose: the markdown subset, rendered safely.</summary>
        public static string Rich(string markdown)
        {
            return MarkdownLite.ToRichText(TextSanitizer.Clean(markdown ?? string.Empty));
        }

        /// <summary>Prose past a label's display cap, as label-sized chunks for a <see cref="Controls.Repeater"/>.</summary>
        public static List<string> RichChunks(string markdown)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(markdown)) { return chunks; }
            foreach (string chunk in Ui.Chunks(markdown, Ui.BlockChunkChars)) { chunks.Add(Rich(chunk)); }
            return chunks;
        }
    }
}
