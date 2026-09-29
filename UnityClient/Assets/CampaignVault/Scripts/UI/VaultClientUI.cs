using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Shared mutable state for every panel. Secrets never touch PlayerPrefs;
    /// only server URL, connector, campaign slug, and tracked ids persist.
    /// </summary>
    public sealed class VaultUiContext
    {
        public VaultClientConfig Config;
        public McpClient Mcp;
        public ByokSettings Byok;
        public OpenAiChatDriver Driver;
        public SystemPromptProvider Prompts;
        public VaultTranscript Transcript = new VaultTranscript();
        public SessionDigest Session;
        public string PartyFingerprint = string.Empty;
        public string PcId = string.Empty;
        public List<string> CompanionIds = new List<string>();
        public JsonValue LastPcEntity;
        public Action OnTranscriptChanged;
    }

    /// <summary>
    /// Builds the whole client UI in code (no hand-edited scene YAML): a dark
    /// vault shell with a tab bar over Chat, Character, Inventory, Companions,
    /// Campaigns, Events, Plugins, and Settings. Add it to an empty scene via
    /// CampaignVault &gt; Create Client UI in the Editor menu.
    /// </summary>
    public class VaultClientUI : MonoBehaviour
    {
        private static readonly string[] Tabs =
        {
            "Chat", "Session", "Dashboard", "Onboard", "Character", "Inventory",
            "Companions", "Campaigns", "Events", "Plugins", "Settings",
        };

        private readonly VaultUiContext _ctx = new VaultUiContext();
        private readonly Dictionary<string, GameObject> _panels = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Button> _tabButtons = new Dictionary<string, Button>();

        private ScrollRect _chatScroll;
        private ChatAutoScroll _chatAutoScroll;
        // Segments already on screen, in order: lets RenderTranscript append
        // (and animate) only what's new instead of rebuilding every line.
        private readonly List<TranscriptSegment> _renderedSegments = new List<TranscriptSegment>();
        private string _activeTab = string.Empty;
        private Transform _chatContent;
        private InputField _chatInput;
        private Text _healthLabel;
        private Text _embeddedLabel;
        private ServerHostManager _server;

        private void Awake()
        {
            _ctx.Config = GetOrAdd<VaultClientConfig>();
            _ctx.Mcp = GetOrAdd<McpClient>();
            _ctx.Byok = GetOrAdd<ByokSettings>();
            _ctx.Prompts = GetOrAdd<SystemPromptProvider>();
            _ctx.Driver = GetOrAdd<OpenAiChatDriver>();
            _ctx.Driver.Byok = _ctx.Byok;
            _ctx.Driver.Vault = _ctx.Config;
            _ctx.Driver.Mcp = _ctx.Mcp;
            _ctx.Driver.Prompts = _ctx.Prompts;
            _ctx.OnTranscriptChanged = RenderTranscript;
            MonoRunner.Bind(this);
            _server = GetOrAdd<ServerHostManager>();
            _server.Config = _ctx.Config;
            _server.Port = PlayerPrefs.GetInt("vault.embedded.port", 5275);
            _server.AutoStart = PlayerPrefs.GetInt("vault.embedded.autostart", 1) == 1;

            VaultFx.Enabled = PlayerPrefs.GetInt("vault.fx", 1) == 1;
            VaultSfx.Muted = PlayerPrefs.GetInt("vault.sfx", 1) == 0;
            _ctx.Config.ServerUrl = PlayerPrefs.GetString("vault.server", _ctx.Config.ServerUrl);
            _ctx.Config.Connector = PlayerPrefs.GetString("vault.connector", "play");
            _ctx.Prompts.CampaignSlug = PlayerPrefs.GetString("vault.campaign", string.Empty);
            _ctx.PcId = PlayerPrefs.GetString("vault.pcid", string.Empty);
            LoadCompanionIds();

            BuildShell();
            ShowTab("Chat");
            int welcomeSkills;
            string welcomeNames;
            string welcomeSkillsLine = _ctx.Prompts.TryGetSkillStatus(out welcomeSkills, out welcomeNames) && welcomeSkills > 0
                ? "DM prompt armed with " + welcomeSkills + " skills."
                : "DM prompt/skills missing — see Settings.";
            _ctx.Transcript.Add(new TranscriptSegment
            {
                Kind = SegmentKind.System,
                Text = "Welcome to the Vault. Set your key in Settings, pick a campaign, then speak. " + welcomeSkillsLine,
            });
            RenderTranscript();
        }

        /// <summary>Shared state, for in-app diagnostics (VaultSmokeRunner).</summary>
        internal VaultUiContext Context { get { return _ctx; } }

        internal GameObject PanelFor(string tab)
        {
            GameObject panel;
            return _panels.TryGetValue(tab, out panel) ? panel : null;
        }

        private System.Collections.IEnumerator Start()
        {
            string smokeUrl = Diagnostics.VaultSmokeRunner.RequestedServerUrl();
            if (smokeUrl != null)
            {
                gameObject.AddComponent<Diagnostics.VaultSmokeRunner>().Run(this, smokeUrl);
                yield break;
            }
            // Autostart only ever hijacks a loopback URL, never a remote server.
            if (!_server.AutoStart || !ServerHostManager.IsLoopbackUrl(_ctx.Config.ServerUrl)) { yield break; }
            bool healthy = false;
            yield return _ctx.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; });
            if (healthy) { yield break; }
            yield return _server.StartEmbedded(delegate (bool ok, string msg)
            {
                Note(ok ? "Embedded server auto-started." : "Embedded autostart: " + msg);
            });
        }

        /// <summary>Stops the embedded server (also covered by OnApplicationQuit) and closes the client.</summary>
        private void QuitApp()
        {
            _server.StopEmbedded();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private T GetOrAdd<T>() where T : Component
        {
            T existing = GetComponent<T>();
            if (existing != null) { return existing; }
            return gameObject.AddComponent<T>();
        }

        private void LoadCompanionIds()
        {
            _ctx.CompanionIds.Clear();
            string raw = PlayerPrefs.GetString("vault.companions", string.Empty);
            if (string.IsNullOrEmpty(raw)) { return; }
            foreach (string id in raw.Split(';'))
            {
                string clean = id.Trim();
                if (!string.IsNullOrEmpty(clean)) { _ctx.CompanionIds.Add(clean); }
            }
        }

        public void SaveCompanionIds()
        {
            PlayerPrefs.SetString("vault.companions", string.Join(";", _ctx.CompanionIds.ToArray()));
            PlayerPrefs.Save();
        }

        /// <summary>
        /// The one place the active campaign changes. Everything scoped to the
        /// old campaign goes with it: chat history (so the model never carries
        /// one table's context into another), session digest, PC/companion ids.
        /// Empty slug = no active campaign (e.g. after deleting it).
        /// </summary>
        public void SelectCampaign(string slug, string system)
        {
            slug = (slug ?? string.Empty).Trim();
            bool changed = slug != _ctx.Prompts.CampaignSlug;
            _ctx.Prompts.CampaignSlug = slug;
            if (!string.IsNullOrEmpty(system)) { _ctx.Prompts.Ruleset = system; }
            if (string.IsNullOrEmpty(slug)) { PlayerPrefs.DeleteKey("vault.campaign"); }
            else { PlayerPrefs.SetString("vault.campaign", slug); }
            if (changed)
            {
                if (string.IsNullOrEmpty(system)) { _ctx.Prompts.Ruleset = string.Empty; }
                _ctx.Prompts.PartyLine = string.Empty;
                _ctx.Prompts.PartyFingerprint = string.Empty;
                _ctx.PartyFingerprint = string.Empty;
                _ctx.Session = null;
                _ctx.LastPcEntity = null;
                _ctx.PcId = string.Empty;
                PlayerPrefs.DeleteKey("vault.pcid");
                _ctx.CompanionIds.Clear();
                SaveCompanionIds();
                _ctx.Driver.ResetConversation();
                if (!string.IsNullOrEmpty(slug))
                {
                    _ctx.Transcript.Add(new TranscriptSegment
                    {
                        Kind = SegmentKind.System,
                        Text = "Campaign \u201c" + slug + "\u201d is now active. The DM starts this table with a fresh conversation.",
                    });
                    RenderTranscript();
                }
            }
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Folds a start_session digest into the client: ruleset, PC roster and
        /// party fingerprint for the DM prompt, and a default PC id for the
        /// Character tab so nobody has to type chars/... by hand.
        /// </summary>
        public void ApplySession(SessionDigest digest)
        {
            _ctx.Session = digest;
            _ctx.PartyFingerprint = digest.Fingerprint;
            _ctx.Prompts.PartyFingerprint = digest.Fingerprint;
            if (!string.IsNullOrEmpty(digest.System)) { _ctx.Prompts.Ruleset = digest.System; }
            var roster = new List<string>();
            bool pcStillInParty = false;
            foreach (var pc in digest.Pcs)
            {
                roster.Add(pc.Id + " \u2014 " + pc.Name);
                if (pc.Id == _ctx.PcId) { pcStillInParty = true; }
            }
            if (roster.Count > 0) { _ctx.Prompts.PartyLine = string.Join("; ", roster.ToArray()); }
            bool trackedNew = false;
            foreach (var companion in digest.Companions)
            {
                if (!_ctx.CompanionIds.Contains(companion.Id)) { _ctx.CompanionIds.Add(companion.Id); trackedNew = true; }
            }
            if (trackedNew) { SaveCompanionIds(); }
            if (!pcStillInParty && digest.Pcs.Count > 0)
            {
                _ctx.PcId = digest.Pcs[0].Id;
                _ctx.LastPcEntity = null;
                PlayerPrefs.SetString("vault.pcid", _ctx.PcId);
                PlayerPrefs.Save();
            }
        }

        private void BuildShell()
        {
            if (EventSystem.current == null)
            {
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            }
            var canvasGo = new GameObject("VaultCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            canvasGo.AddComponent<GraphicRaycaster>();

            var bg = canvasGo.AddComponent<Image>();
            bg.color = VaultTheme.Ink;

            // Atmosphere behind the page (visible through the transparent chat
            // page and margins): drifting embers under a soft vignette.
            var embers = new GameObject("Embers");
            embers.transform.SetParent(canvasGo.transform, false);
            VaultTheme.Stretch(embers.AddComponent<RectTransform>(), 0, 0, 0, 0);
            embers.AddComponent<EmberField>();
            var vignetteGo = new GameObject("Vignette");
            vignetteGo.transform.SetParent(canvasGo.transform, false);
            var vignette = vignetteGo.AddComponent<RawImage>();
            vignette.texture = VaultFx.Vignette();
            vignette.raycastTarget = false;
            VaultTheme.Stretch(vignetteGo.GetComponent<RectTransform>(), 0, 0, 0, 0);

            var root = VaultTheme.Column(canvasGo.transform, "Root", 0);
            VaultTheme.Stretch(root.GetComponent<RectTransform>(), 0, 0, 0, 0);

            var header = VaultTheme.PanelBox(root.transform, "Header", VaultTheme.Panel);
            header.AddComponent<LayoutElement>().minHeight = 76;
            var title = VaultTheme.MakeText(header.transform, "Title", VaultTheme.HeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            title.text = "  \u2726 CAMPAIGN VAULT";
            title.alignment = TextAnchor.MiddleLeft;
            VaultTheme.Stretch(title.GetComponent<RectTransform>(), 8, 8, 4, 4);
            var shimmer = title.gameObject.AddComponent<Breathe>();
            shimmer.Target = title;
            shimmer.From = VaultTheme.Gold;
            shimmer.To = new Color(1f, 0.86f, 0.45f);
            shimmer.Period = 5f;
            var rule = new GameObject("GoldRule");
            rule.transform.SetParent(header.transform, false);
            var ruleImage = rule.AddComponent<Image>();
            ruleImage.color = VaultTheme.GoldDim;
            ruleImage.raycastTarget = false;
            var ruleRect = rule.GetComponent<RectTransform>();
            ruleRect.anchorMin = new Vector2(0f, 0f);
            ruleRect.anchorMax = new Vector2(1f, 0f);
            ruleRect.pivot = new Vector2(0.5f, 0f);
            ruleRect.sizeDelta = new Vector2(0f, 2f);
            ruleRect.anchoredPosition = Vector2.zero;
            var sub = VaultTheme.MakeText(header.transform, "Sub", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            sub.text = "  living world engine · unity client";
            sub.alignment = TextAnchor.LowerLeft;
            VaultTheme.Stretch(sub.GetComponent<RectTransform>(), 8, 8, 4, 26);

            var exit = VaultTheme.MakeButton(header.transform, "Exit", "Exit", 13);
            exit.GetComponent<LayoutElement>().ignoreLayout = true;
            var exitRect = exit.GetComponent<RectTransform>();
            exitRect.anchorMin = exitRect.anchorMax = exitRect.pivot = new Vector2(1f, 1f);
            exitRect.sizeDelta = new Vector2(72f, 30f);
            exitRect.anchoredPosition = new Vector2(-12f, -12f);
            exit.onClick.AddListener(QuitApp);

            var tabRow = VaultTheme.Row(root.transform, "Tabs", 6);
            tabRow.AddComponent<LayoutElement>().minHeight = 44;
            foreach (string tab in Tabs)
            {
                string captured = tab;
                var button = VaultTheme.MakeButton(tabRow.transform, "Tab" + tab, tab, 13);
                button.GetComponent<LayoutElement>().flexibleWidth = 1;
                button.onClick.AddListener(delegate { ShowTab(captured); });
                var underline = new GameObject("Underline");
                underline.transform.SetParent(button.transform, false);
                var underlineImage = underline.AddComponent<Image>();
                underlineImage.color = VaultTheme.Gold;
                underlineImage.raycastTarget = false;
                var underlineRect = underline.GetComponent<RectTransform>();
                underlineRect.anchorMin = new Vector2(0.15f, 0f);
                underlineRect.anchorMax = new Vector2(0.85f, 0f);
                underlineRect.pivot = new Vector2(0.5f, 0f);
                underlineRect.sizeDelta = new Vector2(0f, 2f);
                underline.SetActive(false);
                _tabButtons[tab] = button;
            }

            var content = new GameObject("Content");
            content.transform.SetParent(root.transform, false);
            var contentLayout = content.AddComponent<LayoutElement>();
            contentLayout.flexibleHeight = 1;
            contentLayout.flexibleWidth = 1;

            foreach (string tab in Tabs)
            {
                var panel = new GameObject("Panel" + tab);
                panel.transform.SetParent(content.transform, false);
                VaultTheme.Stretch(panel.AddComponent<RectTransform>(), 0, 0, 0, 0);
                _panels[tab] = panel;
            }

            BuildChatPanel(_panels["Chat"].transform);
            SessionPanel.BuildSessionPanel(_panels["Session"].transform, _ctx, this);
            DashboardPanel.BuildDashboardPanel(_panels["Dashboard"].transform, _ctx, this);
            OnboardWizard.BuildOnboardPanel(_panels["Onboard"].transform, _ctx, this);
            PartyPanels.BuildCharacterPanel(_panels["Character"].transform, _ctx, this);
            PartyPanels.BuildInventoryPanel(_panels["Inventory"].transform, _ctx, this);
            PartyPanels.BuildCompanionsPanel(_panels["Companions"].transform, _ctx, this);
            WorldPanels.BuildCampaignsPanel(_panels["Campaigns"].transform, _ctx, this);
            WorldPanels.BuildEventsPanel(_panels["Events"].transform, _ctx);
            WorldPanels.BuildPluginsPanel(_panels["Plugins"].transform, _ctx);
            BuildSettingsPanel(_panels["Settings"].transform);
        }

        public void ShowTab(string tab)
        {
            bool switching = tab != _activeTab;
            _activeTab = tab;
            foreach (var kv in _panels)
            {
                kv.Value.SetActive(kv.Key == tab);
                if (switching && kv.Key == tab) { VaultFx.FadeIn(kv.Value, 0.22f, 0.985f); }
            }
            foreach (var kv in _tabButtons)
            {
                bool active = kv.Key == tab;
                kv.Value.GetComponentInChildren<Text>().color = active ? VaultTheme.Gold : VaultTheme.Parchment;
                kv.Value.transform.Find("Underline").gameObject.SetActive(active);
            }
        }

        // ---- Chat ----

        private void BuildChatPanel(Transform parent)
        {
            var column = VaultTheme.Column(parent, "ChatCol", 8);
            VaultTheme.Stretch(column.GetComponent<RectTransform>(), 8, 8, 8, 8);

            _chatScroll = VaultTheme.MakeScrollView(column.transform, "ChatScroll");
            _chatScroll.GetComponent<LayoutElement>().flexibleHeight = 1;
            _chatContent = _chatScroll.content;
            _chatAutoScroll = _chatScroll.gameObject.AddComponent<ChatAutoScroll>();
            _chatAutoScroll.Scroll = _chatScroll;

            var thinking = VaultTheme.MakeText(column.transform, "Thinking", VaultTheme.SmallSize + 1, VaultTheme.Gold, FontStyle.Italic, VaultTheme.BodyFont);
            thinking.raycastTarget = false;
            thinking.gameObject.AddComponent<LayoutElement>().minHeight = 20;
            var indicator = thinking.gameObject.AddComponent<ThinkingIndicator>();
            indicator.Label = thinking;
            indicator.IsBusy = delegate { return _ctx.Driver != null && _ctx.Driver.IsBusy; };

            var inputRow = VaultTheme.Row(column.transform, "InputRow", 8);
            inputRow.AddComponent<LayoutElement>().minHeight = 40;
            _chatInput = VaultTheme.MakeInput(inputRow.transform, "ChatInput", "Speak or act\u2026 (Enter to send)", false);
            var send = VaultTheme.GoldButton(inputRow.transform, "Send", "Send", 16);
            send.GetComponent<LayoutElement>().minWidth = 110;
            send.onClick.AddListener(SendChat);
            _chatInput.onEndEdit.AddListener(delegate (string text)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    SendChat();
                }
            });
        }

        private void SendChat()
        {
            string text = _chatInput.text.Trim();
            if (string.IsNullOrEmpty(text)) { return; }
            _chatInput.text = string.Empty;
            SendChatText(text);
        }

        /// <summary>Panels call this to route item/world actions through the DM driver.</summary>
        public void SendChatText(string text)
        {
            ShowTab("Chat");
            if (_ctx.Driver.IsBusy)
            {
                Note("The DM is still resolving the last action.");
                return;
            }
            Typewriter.CompleteAll();
            _ctx.Transcript.Add(new TranscriptSegment { Kind = SegmentKind.Player, Text = text });
            if (_chatAutoScroll != null) { _chatAutoScroll.Pin(); }
            RenderTranscript();
            StartCoroutine(_ctx.Driver.SendPlayerText(text, _ctx.Transcript, RenderTranscript));
        }

        /// <summary>
        /// Appends (and animates) only segments that aren't on screen yet. When
        /// the transcript was rewritten underneath (cap trimming, a clear),
        /// rebuilds everything once, without replaying entrances.
        /// </summary>
        private void RenderTranscript()
        {
            if (_chatContent == null) { return; }
            var segments = _ctx.Transcript.Segments;
            bool appendOnly = _renderedSegments.Count <= segments.Count;
            for (int i = 0; appendOnly && i < _renderedSegments.Count; i++)
            {
                if (!ReferenceEquals(_renderedSegments[i], segments[i])) { appendOnly = false; }
            }
            if (!appendOnly)
            {
                Typewriter.CompleteAll();
                VaultTheme.ClearChildren(_chatContent);
                _renderedSegments.Clear();
            }
            for (int i = _renderedSegments.Count; i < segments.Count; i++)
            {
                RenderSegment(_chatContent, segments[i], appendOnly);
                _renderedSegments.Add(segments[i]);
            }
        }

        private static void RenderSegment(Transform parent, TranscriptSegment seg, bool animate)
        {
            GameObject line = null;
            switch (seg.Kind)
            {
                case SegmentKind.Narration:
                    var narration = VaultTheme.NarrationText(parent, "Narration", seg.Text);
                    if (animate) { Typewriter.Begin(narration); }
                    line = narration.gameObject;
                    break;
                case SegmentKind.NpcVoice:
                    line = RenderVoice(parent, seg, animate);
                    break;
                case SegmentKind.Roll:
                    line = RenderRoll(parent, seg, animate);
                    break;
                case SegmentKind.Player:
                    var player = VaultTheme.MakeText(parent, "Player", VaultTheme.BodySize - 1, VaultTheme.Arcane, FontStyle.Italic, VaultTheme.BodyFont);
                    player.text = "\u00bb  " + seg.Text;
                    VaultTheme.FitVertical(player);
                    line = player.gameObject;
                    break;
                case SegmentKind.System:
                    var sys = VaultTheme.MakeText(parent, "System", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                    sys.text = seg.Text;
                    VaultTheme.FitVertical(sys);
                    line = sys.gameObject;
                    break;
                case SegmentKind.ToolData:
                    var tool = VaultTheme.MakeText(parent, "Tool", VaultTheme.SmallSize, VaultTheme.Faint, FontStyle.Normal, VaultTheme.MonoFont);
                    tool.text = seg.Text;
                    VaultTheme.FitVertical(tool);
                    line = tool.gameObject;
                    break;
            }
            if (animate && line != null) { VaultFx.FadeIn(line); }
        }

        private static GameObject RenderVoice(Transform parent, TranscriptSegment seg, bool animate)
        {
            var row = VaultTheme.Row(parent, "Voice", 8);
            var bar = new GameObject("Bar");
            bar.transform.SetParent(row.transform, false);
            bar.AddComponent<Image>().color = VaultTheme.SpeakerColor(seg.Speaker);
            var barLayout = bar.AddComponent<LayoutElement>();
            barLayout.minWidth = 3;
            barLayout.flexibleHeight = 1;
            var col = VaultTheme.Column(row.transform, "VoiceCol", 2);
            col.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            var name = VaultTheme.MakeText(col.transform, "Speaker", VaultTheme.SmallSize + 1, VaultTheme.SpeakerColor(seg.Speaker), FontStyle.Bold, VaultTheme.BodyFont);
            name.text = seg.Speaker.ToUpperInvariant();
            VaultTheme.FitVertical(name);
            var line = VaultTheme.MakeText(col.transform, "Line", VaultTheme.BodySize, VaultTheme.Parchment, FontStyle.Italic, VaultTheme.BodyFont);
            line.text = "\u201C" + seg.Text + "\u201D";
            VaultTheme.FitVertical(line);
            if (animate) { Typewriter.Begin(line); }
            return row;
        }

        private static GameObject RenderRoll(Transform parent, TranscriptSegment seg, bool animate)
        {
            var chip = VaultTheme.PanelBox(parent, "Roll", VaultTheme.RollBg);
            var layout = chip.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10;
            layout.padding = new RectOffset(12, 12, 8, 8);
            layout.childAlignment = TextAnchor.MiddleLeft;
            chip.AddComponent<LayoutElement>().minHeight = 38;
            var dice = VaultTheme.MakeText(chip.transform, "Dice", VaultTheme.RollSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.MonoFont);
            dice.text = "\u2694 " + seg.Roll.Label + " \u2014 " + seg.Roll.Detail;
            var outcome = VaultTheme.MakeText(chip.transform, "Outcome", VaultTheme.RollSize, seg.Roll.Success ? VaultTheme.Leaf : VaultTheme.Blood, FontStyle.Bold, VaultTheme.MonoFont);
            outcome.text = seg.Roll.Success ? "SUCCESS" : "FAIL";
            outcome.alignment = TextAnchor.MiddleCenter;
            outcome.gameObject.AddComponent<LayoutElement>().minWidth = 90;
            if (animate)
            {
                var tumble = chip.AddComponent<DiceTumble>();
                tumble.Outcome = outcome;
                tumble.Background = chip.GetComponent<Image>();
                tumble.BaseBackground = VaultTheme.RollBg;
                tumble.Success = seg.Roll.Success;
                tumble.FinalText = outcome.text;
                tumble.FinalColor = outcome.color;
            }
            return chip;
        }

        /// <summary>
        /// /health alone only proves the process is up; a green light here also
        /// requires an MCP session and a tools/list on /play, which is what every
        /// panel and the DM driver actually depend on.
        /// </summary>
        private System.Collections.IEnumerator CheckConnection()
        {
            _healthLabel.text = "Checking\u2026";
            _healthLabel.color = VaultTheme.Muted;
            bool healthy = false;
            string healthMessage = string.Empty;
            yield return _ctx.Config.CheckHealth(delegate (bool ok, string msg) { healthy = ok; healthMessage = msg; });
            if (!healthy)
            {
                _healthLabel.text = "Server: " + healthMessage;
                _healthLabel.color = VaultTheme.Blood;
                yield break;
            }
            _ctx.Mcp.ResetSessions();
            McpOutcome<List<McpToolInfo>> tools = null;
            yield return _ctx.Mcp.ListTools(_ctx.Config, "play", delegate (McpOutcome<List<McpToolInfo>> o) { tools = o; });
            if (tools == null || !tools.Ok)
            {
                _healthLabel.text = "Server is up, but MCP failed: " + (tools != null ? tools.ErrorMessage : "no response");
                _healthLabel.color = VaultTheme.Blood;
                yield break;
            }
            _healthLabel.text = "Server: healthy \u00b7 MCP connected (" + tools.Data.Count + " play tools)";
            _healthLabel.color = VaultTheme.Leaf;
        }

        // ---- Settings ----

        private void BuildSettingsPanel(Transform parent)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "SettingsScroll");
            var col = VaultTheme.Column(scroll.content, "SettingsCol", 10);

            AddSection(col.transform, "Server");
            var serverInput = VaultTheme.MakeInput(col.transform, "ServerUrl", "http://localhost:5275", false);
            serverInput.text = _ctx.Config.ServerUrl;
            var connectorRow = VaultTheme.Row(col.transform, "ConnectorRow", 8);
            var playButton = VaultTheme.MakeButton(connectorRow.transform, "Play", "Connector: Play", 14);
            var buildButton = VaultTheme.MakeButton(connectorRow.transform, "Build", "Connector: Build", 14);
            playButton.onClick.AddListener(delegate
            {
                _ctx.Config.Connector = "play";
                _ctx.Driver.InvalidateTools();
                PlayerPrefs.SetString("vault.connector", "play");
            });
            buildButton.onClick.AddListener(delegate
            {
                _ctx.Config.Connector = "build";
                _ctx.Driver.InvalidateTools();
                PlayerPrefs.SetString("vault.connector", "build");
            });
            var tokenInput = VaultTheme.MakeInput(col.transform, "Token", "Server bearer token (optional, memory-only)", true);
            var tokenRow = VaultTheme.Row(col.transform, "TokenRow", 8);
            var setToken = VaultTheme.MakeButton(tokenRow.transform, "SetToken", "Set token", 14);
            setToken.onClick.AddListener(delegate
            {
                _ctx.Config.SetBearerToken(tokenInput.text);
                _ctx.Mcp.ResetSessions();
                tokenInput.text = string.Empty;
                Note("Bearer token kept in memory only.");
            });
            var clearToken = VaultTheme.MakeButton(tokenRow.transform, "ClearToken", "Clear", 14);
            clearToken.onClick.AddListener(delegate { _ctx.Config.ClearSecrets(); _ctx.Mcp.ResetSessions(); Note("Bearer token cleared."); });
            var saveServer = VaultTheme.GoldButton(col.transform, "SaveServer", "Save server URL", 14);
            saveServer.onClick.AddListener(delegate
            {
                _ctx.Config.ServerUrl = serverInput.text.Trim();
                PlayerPrefs.SetString("vault.server", _ctx.Config.ServerUrl);
                PlayerPrefs.Save();
                Note("Server URL saved.");
            });
            var healthButton = VaultTheme.MakeButton(col.transform, "Health", "Check server health", 14);
            _healthLabel = VaultTheme.MakeText(col.transform, "HealthLabel", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            VaultTheme.FitVertical(_healthLabel);
            healthButton.onClick.AddListener(delegate { StartCoroutine(CheckConnection()); });

            AddSection(col.transform, "Table feel");
            var feelRow = VaultTheme.Row(col.transform, "FeelRow", 8);
            var fxButton = VaultTheme.MakeButton(feelRow.transform, "Fx", string.Empty, 14);
            fxButton.GetComponent<LayoutElement>().minWidth = 200;
            var sfxButton = VaultTheme.MakeButton(feelRow.transform, "Sfx", string.Empty, 14);
            sfxButton.GetComponent<LayoutElement>().minWidth = 200;
            System.Action paintFeel = delegate
            {
                fxButton.GetComponentInChildren<Text>().text = "Animations: " + (VaultFx.Enabled ? "on" : "off");
                sfxButton.GetComponentInChildren<Text>().text = "Sound: " + (VaultSfx.Muted ? "off" : "on");
            };
            paintFeel();
            fxButton.onClick.AddListener(delegate
            {
                VaultFx.Enabled = !VaultFx.Enabled;
                PlayerPrefs.SetInt("vault.fx", VaultFx.Enabled ? 1 : 0);
                PlayerPrefs.Save();
                paintFeel();
            });
            sfxButton.onClick.AddListener(delegate
            {
                VaultSfx.Muted = !VaultSfx.Muted;
                PlayerPrefs.SetInt("vault.sfx", VaultSfx.Muted ? 0 : 1);
                PlayerPrefs.Save();
                paintFeel();
            });

            AddSection(col.transform, "AI provider (BYOK)");
            var baseInput = VaultTheme.MakeInput(col.transform, "BaseUrl", "https://api.openai.com/v1", false);
            baseInput.text = _ctx.Byok.BaseUrl;
            var modelInput = VaultTheme.MakeInput(col.transform, "Model", "gpt-4o", false);
            modelInput.text = _ctx.Byok.Model;
            var keyInput = VaultTheme.MakeInput(col.transform, "ApiKey", "API key (session-only, never stored)", true);
            var keyRow = VaultTheme.Row(col.transform, "KeyRow", 8);
            var setKey = VaultTheme.MakeButton(keyRow.transform, "SetKey", "Set key", 14);
            setKey.onClick.AddListener(delegate
            {
                _ctx.Byok.SetApiKey(keyInput.text);
                keyInput.text = string.Empty;
                Note("API key kept in memory for this session.");
            });
            var clearKey = VaultTheme.MakeButton(keyRow.transform, "ClearKey", "Forget key", 14);
            clearKey.onClick.AddListener(delegate { _ctx.Byok.ClearSecrets(); Note("API key forgotten."); });
            var saveByok = VaultTheme.GoldButton(col.transform, "SaveByok", "Save endpoint + model", 14);
            saveByok.onClick.AddListener(delegate
            {
                _ctx.Byok.BaseUrl = baseInput.text.Trim();
                _ctx.Byok.Model = modelInput.text.Trim();
                _ctx.Byok.SaveNonSecrets();
                Note("Endpoint and model saved (key never is).");
            });

            BuildEmbeddedSection(col.transform);

            AddSection(col.transform, "DM prompt + skills");
            var skillsLabel = VaultTheme.MakeText(col.transform, "Skills", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            VaultTheme.FitVertical(skillsLabel);
            int skillCount;
            string skillNames;
            if (_ctx.Prompts.TryGetSkillStatus(out skillCount, out skillNames) && skillCount > 0)
            {
                skillsLabel.text = "system-prompt.md + " + skillCount + " skills: " + skillNames;
                skillsLabel.color = VaultTheme.Leaf;
            }
            else
            {
                skillsLabel.text = "Prompt/skills not staged. Re-run the StreamingAssets copy in the client README, then reopen.";
                skillsLabel.color = VaultTheme.Blood;
            }
        }

        private void BuildEmbeddedSection(Transform col)
        {
            AddSection(col, "Embedded server");
            var about = VaultTheme.MakeText(col, "EmbAbout", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            about.text = "Ships inside the build (StreamingAssets). Launches on 127.0.0.1 only, no token, data under local storage. Desktop builds only.";
            VaultTheme.FitVertical(about);

            var portRow = VaultTheme.Row(col, "PortRow", 8);
            var portInput = VaultTheme.MakeInput(portRow.transform, "Port", "5275", false);
            portInput.text = _server.Port.ToString();
            portInput.GetComponent<LayoutElement>().minWidth = 120;
            var autoButton = VaultTheme.MakeButton(portRow.transform, "Auto", string.Empty, 14);
            autoButton.GetComponent<LayoutElement>().minWidth = 150;
            var autoLabel = autoButton.GetComponentInChildren<Text>();
            System.Action repaintAuto = delegate
            {
                autoLabel.text = _server.AutoStart ? "[x] autostart" : "[ ] autostart";
            };
            repaintAuto();
            autoButton.onClick.AddListener(delegate
            {
                _server.AutoStart = !_server.AutoStart;
                PlayerPrefs.SetInt("vault.embedded.autostart", _server.AutoStart ? 1 : 0);
                PlayerPrefs.Save();
                repaintAuto();
            });

            var runRow = VaultTheme.Row(col, "RunRow", 8);
            var toggle = VaultTheme.GoldButton(runRow.transform, "RunStop", "Start embedded", 14);
            toggle.onClick.AddListener(delegate
            {
                int port;
                if (!int.TryParse(portInput.text.Trim(), out port)) { port = _server.Port; }
                _server.Port = port;
                PlayerPrefs.SetInt("vault.embedded.port", port);
                PlayerPrefs.Save();
                if (_server.IsRunning)
                {
                    _server.StopEmbedded();
                    PaintEmbedded("Embedded server stopped.");
                }
                else
                {
                    PaintEmbedded("Starting embedded server\u2026");
                    StartCoroutine(_server.StartEmbedded(delegate (bool ok, string msg)
                    {
                        PaintEmbedded(msg);
                        Note(msg);
                    }));
                }
            });

            _embeddedLabel = VaultTheme.MakeText(col, "EmbStatus", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            VaultTheme.FitVertical(_embeddedLabel);
            PaintEmbedded(_server.IsRunning ? "Embedded server running." : "Embedded server idle.");
        }

        private void PaintEmbedded(string message)
        {
            if (_embeddedLabel == null) { return; }
            _embeddedLabel.text = message;
            _embeddedLabel.color = _server.IsRunning ? VaultTheme.Leaf : VaultTheme.Muted;
        }

        private static void AddSection(Transform parent, string title)
        {
            var text = VaultTheme.MakeText(parent, "Section" + title, VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            text.text = title.ToUpperInvariant();
            VaultTheme.FitVertical(text);
        }

        public void Note(string message)
        {
            _ctx.Transcript.Add(new TranscriptSegment { Kind = SegmentKind.System, Text = message });
            if (_ctx.OnTranscriptChanged != null) { _ctx.OnTranscriptChanged(); }
        }
    }
}
