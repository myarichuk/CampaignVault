using System.Collections;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Diagnostics;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// The client's composition root: wires the services, state and controller
    /// on one GameObject, restores preferences, and on Start either runs the
    /// smoke scenario (-vault-smoke) or autostarts the embedded server. Views
    /// find it and bind to State/Controller; it holds no UI itself.
    /// </summary>
    [DisallowMultipleComponent]
    public class VaultBootstrap : MonoBehaviour
    {
        public VaultAppState State { get; private set; }
        public VaultController Controller { get; private set; }

        private void Awake() { Initialize(null); }

        /// <summary>Idempotent. Tests call it directly (edit mode runs no Awake) with in-memory prefs.</summary>
        public void Initialize(IVaultPrefs prefs)
        {
            if (State != null) { return; }
            var state = new VaultAppState
            {
                Config = GetOrAdd<VaultClientConfig>(),
                Mcp = GetOrAdd<McpClient>(),
                Byok = GetOrAdd<ByokSettings>(),
                Prompts = GetOrAdd<SystemPromptProvider>(),
                Driver = GetOrAdd<OpenAiChatDriver>(),
                Server = GetOrAdd<ServerHostManager>(),
            };
            state.Driver.Byok = state.Byok;
            state.Driver.Vault = state.Config;
            state.Driver.Mcp = state.Mcp;
            state.Driver.Prompts = state.Prompts;
            state.Server.Config = state.Config;

            // A smoke run never touches the player's saved prefs.
            if (prefs == null) { prefs = VaultSmokeRunner.RequestedServerUrl() != null ? (IVaultPrefs)new MemoryPrefs() : new UnityPrefs(); }
            State = state;
            Controller = new VaultController(state, this, prefs);
            Controller.LoadPreferences();

            int skills;
            string names;
            bool armed = state.Prompts.TryGetSkillStatus(out skills, out names) && skills > 0;
            state.Transcript.Add(new TranscriptSegment
            {
                Kind = SegmentKind.System,
                Text = "Welcome to the Vault. " + (armed
                    ? "The DM is armed with " + skills + " skills."
                    : "The DM prompt or skills are missing — see Settings."),
            });
        }

        /// <summary>First run: no usable provider yet, so the UI should open setup.</summary>
        public bool NeedsSetup
        {
            get
            {
                string reason;
                return VaultSmokeRunner.RequestedServerUrl() == null && !State.Byok.Validate(out reason);
            }
        }

        private IEnumerator Start()
        {
            string smokeUrl = VaultSmokeRunner.RequestedServerUrl();
            if (smokeUrl != null)
            {
                GetOrAdd<VaultSmokeRunner>().Run(this, smokeUrl);
                yield break;
            }
            yield return Controller.AutostartEmbedded();
        }

        /// <summary>Stops the embedded server and closes the client.</summary>
        public void Quit()
        {
            if (State != null && State.Server != null) { State.Server.StopEmbedded(); }
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private T GetOrAdd<T>() where T : Component
        {
            T existing = GetComponent<T>();
            return existing != null ? existing : gameObject.AddComponent<T>();
        }
    }
}
