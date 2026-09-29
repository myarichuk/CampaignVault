using UnityEditor;
using UnityEngine;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// Adds the client bootstrap to the open scene without hand-editing scene
    /// YAML: CampaignVault &gt; Create Client UI.
    /// </summary>
    public static class VaultClientMenu
    {
        [MenuItem("CampaignVault/Create Client UI")]
        public static void CreateClientUI()
        {
            var go = new GameObject("VaultClient");
            go.AddComponent<VaultClientUI>();
            Selection.activeGameObject = go;
            Debug.Log("[Vault] VaultClient added. Press Play, then open Settings to add your key.");
        }
    }
}
