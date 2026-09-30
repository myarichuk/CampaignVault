using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// Renders UI into a 1920x1080 render texture and saves a PNG under
    /// Library/VaultSnapshots (Temp/ is wiped when the editor quits), so layout and theme can be reviewed without a
    /// human at the editor. Asserts only that the frame isn't blank.
    /// </summary>
    public class UiSnapshotTests
    {
        public static string SnapshotDir
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "VaultSnapshots")); }
        }

        public static IEnumerator Capture(VisualTreeAsset tree, string name, System.Action<VisualElement> prepare, int width = 1920, int height = 1080)
        {
            var settings = Object.Instantiate(Resources.Load<PanelSettings>("VaultUI/VaultPanelSettings"));
            Assert.IsNotNull(settings, "VaultPanelSettings missing: run CampaignVault > Build UI Assets");
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            settings.targetTexture = rt;
            var go = new GameObject("Snapshot-" + name);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = tree;
            yield return null;
            if (prepare != null) { prepare(doc.rootVisualElement); }
            // Dynamic font atlases fill on first use, and transitions run in real time
            // (batchmode frames are uncapped): wait real time, not a frame count.
            float until = Time.realtimeSinceStartup + 0.8f;
            while (Time.realtimeSinceStartup < until) { yield return null; }

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            Directory.CreateDirectory(SnapshotDir);
            string path = Path.Combine(SnapshotDir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[VaultSnapshot] " + path);

            var center = tex.GetPixel(width / 2, height / 2);
            var corner = tex.GetPixel(4, 4);
            Object.Destroy(go);
            rt.Release();
            Assert.IsTrue(center != Color.clear || corner != Color.clear, "snapshot is blank");
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator StyleGuide()
        {
            var tree = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/CampaignVault/UI/Dev/StyleGuide.uxml");
            Assert.IsNotNull(tree);
            yield return Capture(tree, "styleguide", null);
        }
#endif
    }
}
