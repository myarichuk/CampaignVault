using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using Unity.VectorGraphics.Editor;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// Imports every SVG under the UI folder as a UI Toolkit VectorImage, so
    /// icons and frame ornaments stay crisp at any scale and tint from USS.
    /// </summary>
    public class VaultUiSvgPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            if (!assetPath.StartsWith(VaultUiAssetBuilder.UiRoot + "/") || !assetPath.EndsWith(".svg")) { return; }
            var importer = assetImporter as SVGImporter;
            if (importer != null && importer.SvgType != SVGType.VectorImage) { importer.SvgType = SVGType.VectorImage; }
        }
    }

    /// <summary>
    /// Generates the UI assets that need Unity APIs rather than a text editor:
    /// SDF font assets (with bold/italic weight tables, so rich-text &lt;b&gt;
    /// and &lt;i&gt; use the real faces) and the PanelSettings the shell loads
    /// from Resources. Idempotent; re-run after adding a font.
    ///   Editor: CampaignVault &gt; Build UI Assets
    ///   CLI:    unity run . -- -executeMethod CampaignVault.UnityClient.Editor.VaultUiAssetBuilder.BuildAll
    /// </summary>
    public static class VaultUiAssetBuilder
    {
        public const string UiRoot = "Assets/CampaignVault/UI";
        private const string FontDir = UiRoot + "/Fonts";
        private const string SdfDir = FontDir + "/SDF";
        private const string PanelSettingsPath = UiRoot + "/Resources/VaultUI/VaultPanelSettings.asset";
        private const string ThemePath = UiRoot + "/Theme/VaultTheme.tss";

        [MenuItem("CampaignVault/Build UI Assets")]
        public static void BuildAll()
        {
            Directory.CreateDirectory(SdfDir);
            AssetDatabase.Refresh();

            var cinzel = Font("Cinzel-Regular");
            var cinzelSemi = Font("Cinzel-SemiBold");
            var cinzelBold = Font("Cinzel-Bold");
            Weights(cinzel, null, cinzelSemi, cinzelBold, null);

            var garamond = Font("EBGaramond-Regular");
            var garamondItalic = Font("EBGaramond-Italic");
            var garamondMedium = Font("EBGaramond-Medium");
            var garamondSemi = Font("EBGaramond-SemiBold");
            var garamondSemiItalic = Font("EBGaramond-SemiBoldItalic");
            var garamondBold = Font("EBGaramond-Bold");
            Weights(garamond, garamondItalic, garamondSemi, garamondBold, garamondSemiItalic);
            Weights(garamondMedium, garamondItalic, garamondSemi, garamondBold, garamondSemiItalic);

            var mono = Font("JetBrainsMono-Regular");
            var monoBold = Font("JetBrainsMono-Bold");
            Weights(mono, null, null, monoBold, null);

            foreach (var fa in new[] { cinzel, cinzelSemi, cinzelBold, garamond, garamondItalic, garamondMedium, garamondSemi, garamondSemiItalic, garamondBold, mono, monoBold })
            {
                EditorUtility.SetDirty(fa);
            }

            BuildPanelSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Vault] UI assets built.");
        }

        /// <summary>A dynamic SDF font asset next to its TTF, created once and reused.</summary>
        private static FontAsset Font(string name)
        {
            string path = SdfDir + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(path);
            if (existing != null) { return existing; }
            var ttf = AssetDatabase.LoadAssetAtPath<Font>(FontDir + "/" + name + ".ttf");
            if (ttf == null) { throw new FileNotFoundException("Missing font " + FontDir + "/" + name + ".ttf"); }
            var fa = FontAsset.CreateFontAsset(ttf, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            fa.name = name;
            AssetDatabase.CreateAsset(fa, path);
            foreach (var atlas in fa.atlasTextures)
            {
                if (atlas == null) { continue; }
                atlas.name = name + " Atlas";
                AssetDatabase.AddObjectToAsset(atlas, fa);
            }
            fa.material.name = name + " Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            return fa;
        }

        /// <summary>Weight table slots: 4 = regular (italic face), 6 = semibold, 7 = bold (rich-text &lt;b&gt;).</summary>
        private static void Weights(FontAsset regular, FontAsset italic, FontAsset semibold, FontAsset bold, FontAsset semiboldItalic)
        {
            var table = regular.fontWeightTable;
            if (italic != null) { table[4].italicTypeface = italic; }
            if (semibold != null) { table[6].regularTypeface = semibold; }
            if (semiboldItalic != null) { table[6].italicTypeface = semiboldItalic; }
            if (bold != null) { table[7].regularTypeface = bold; }
            if (semiboldItalic != null) { table[7].italicTypeface = semiboldItalic; }
        }

        private static void BuildPanelSettings()
        {
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null) { throw new FileNotFoundException("Missing theme " + ThemePath); }
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            bool created = settings == null;
            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PanelSettingsPath));
                settings = ScriptableObject.CreateInstance<PanelSettings>();
            }
            settings.themeStyleSheet = theme;
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;
            settings.clearColor = true;
            settings.colorClearValue = new Color(0.027f, 0.031f, 0.043f, 1f);
            if (created) { AssetDatabase.CreateAsset(settings, PanelSettingsPath); }
            else { EditorUtility.SetDirty(settings); }
        }
    }
}
