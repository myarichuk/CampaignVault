using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Properties;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>
    /// The AI provider editor (Templates/Settings/ProviderForm.uxml): profiles, preset, endpoint, model, sampling,
    /// streaming, key. Settings and first-run Setup each show one; both follow StateArea.Providers.
    /// </summary>
    /// <remarks>
    /// The fields hold what's being typed. They take the active profile's values only when the profile changed under
    /// them (another profile or preset picked), never on a refresh the form caused itself, so typing is never undone.
    /// </remarks>
    public sealed class ProviderFormViewModel : ViewModel, ITemplated
    {
        private static readonly string[] EffortValues = { string.Empty, "low", "medium", "high" };
        private static readonly string[] EffortLabels = { "DEFAULT", "LOW", "MEDIUM", "HIGH" };

        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private List<ChoiceViewModel> _profiles = new List<ChoiceViewModel>();
        private List<ChoiceViewModel> _presets = new List<ChoiceViewModel>();
        private readonly List<ChoiceViewModel> _efforts = new List<ChoiceViewModel>();
        private string _name = string.Empty;
        private string _baseUrl = string.Empty;
        private string _model = string.Empty;
        private string _temperature = string.Empty;
        private string _maxTokens = string.Empty;
        private string _effort = string.Empty;
        private bool _stream = true;
        private bool _storyteller = true;
        private string _priceIn = string.Empty;
        private string _priceCached = string.Empty;
        private string _priceOut = string.Empty;
        private string _spend = string.Empty;
        private string _key = string.Empty;
        private string _keyPlaceholder = string.Empty;
        private string _keyState = string.Empty;
        private string _status = string.Empty;
        private string _statusTone = "muted";
        private bool _canDelete;
        private string _seen;
        private bool _saving;

        public ProviderFormViewModel(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            for (int i = 0; i < EffortValues.Length; i++)
            {
                string value = EffortValues[i];
                // Choosing an effort changes the form only: it is saved with the rest.
                _efforts.Add(new ChoiceViewModel(value, EffortLabels[i], null, true, delegate { Effort = value; }, "effort-" + (value.Length == 0 ? "default" : value)));
            }
            NewProfile = delegate { controller.AddProfile("openai"); };
            Save = DoSave;
            Test = DoTest;
            Forget = DoForget;
            ResetSpend = delegate { controller.ResetCampaignUsage(); };
            Delete = delegate { controller.DeleteProfile(); };
            Note = DisplayText.Plain("Keys are stored as plain text in " + ByokSettings.FilePath + " on this device and are only ever sent to the endpoint above.");
            PricesNote = DisplayText.Plain("Leave blank to use the bundled prices (checked " + ModelPricing.Bundled.AsOf + "). Fill these in for a custom endpoint or a model the table doesn't know; they win over the table.");
            Watch(state, StateArea.Providers | StateArea.Driver | StateArea.Campaign);
            Refresh();
        }

        public string Template { get { return "Settings/ProviderForm"; } }

        [CreateProperty] public List<ChoiceViewModel> Profiles { get { return _profiles; } private set { SetList(ref _profiles, value); } }
        [CreateProperty] public List<ChoiceViewModel> Presets { get { return _presets; } private set { SetList(ref _presets, value); } }
        [CreateProperty] public List<ChoiceViewModel> Efforts { get { return _efforts; } }
        [CreateProperty] public string Name { get { return _name; } set { Set(ref _name, value ?? string.Empty); } }
        [CreateProperty] public string BaseUrl { get { return _baseUrl; } set { Set(ref _baseUrl, value ?? string.Empty); } }
        [CreateProperty] public string Model { get { return _model; } set { Set(ref _model, value ?? string.Empty); } }
        [CreateProperty] public string Temperature { get { return _temperature; } set { Set(ref _temperature, value ?? string.Empty); } }
        [CreateProperty] public string MaxTokens { get { return _maxTokens; } set { Set(ref _maxTokens, value ?? string.Empty); } }
        [CreateProperty]
        public string Effort
        {
            get { return _effort; }
            set { if (Set(ref _effort, value ?? string.Empty)) { PaintEfforts(); } }
        }
        [CreateProperty] public bool Stream { get { return _stream; } set { Set(ref _stream, value); } }
        /// <summary>A separate pass writes the scene from a call that sees no tool data.</summary>
        [CreateProperty] public bool Storyteller { get { return _storyteller; } set { Set(ref _storyteller, value); } }
        [CreateProperty] public string PriceIn { get { return _priceIn; } set { Set(ref _priceIn, value ?? string.Empty); } }
        [CreateProperty] public string PriceCached { get { return _priceCached; } set { Set(ref _priceCached, value ?? string.Empty); } }
        [CreateProperty] public string PriceOut { get { return _priceOut; } set { Set(ref _priceOut, value ?? string.Empty); } }
        [CreateProperty] public string PricesNote { get; private set; }
        /// <summary>What the open campaign has cost so far, or why there is no counter.</summary>
        [CreateProperty] public string Spend { get { return _spend; } private set { Set(ref _spend, value); } }
        [CreateProperty] public Action ResetSpend { get; private set; }
        /// <summary>A key being pasted: never shown again, never read back from the profile.</summary>
        [CreateProperty] public string Key { get { return _key; } set { Set(ref _key, value ?? string.Empty); } }
        [CreateProperty] public string KeyPlaceholder { get { return _keyPlaceholder; } private set { Set(ref _keyPlaceholder, value); } }
        [CreateProperty] public string KeyState { get { return _keyState; } private set { Set(ref _keyState, value); } }
        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        /// <summary>leaf (worked), blood (failed) or muted (pending): a class suffix.</summary>
        [CreateProperty] public string StatusTone { get { return _statusTone; } private set { Set(ref _statusTone, value); } }
        [CreateProperty] public bool CanDelete { get { return _canDelete; } private set { Set(ref _canDelete, value); } }
        [CreateProperty] public string Note { get; private set; }
        [CreateProperty] public Action NewProfile { get; private set; }
        [CreateProperty] public Action Save { get; private set; }
        [CreateProperty] public Action Test { get; private set; }
        [CreateProperty] public Action Forget { get; private set; }
        [CreateProperty] public Action Delete { get; private set; }

        public override void Refresh()
        {
            var byok = _s.Byok;
            var profile = byok.Active;

            var profiles = new List<ChoiceViewModel>();
            for (int i = 0; i < byok.Profiles.Count; i++)
            {
                int index = i;
                var chip = FindChoice(_profiles, index.ToString(CultureInfo.InvariantCulture)) ?? new ChoiceViewModel(index.ToString(CultureInfo.InvariantCulture), string.Empty, null, true, delegate { _c.SelectProfile(index); }, "profile-" + index);
                bool active = i == byok.ActiveIndex;
                chip.Update(DisplayText.Plain(byok.Profiles[i].Name), active ? "check" : null, active);
                profiles.Add(chip);
            }
            Profiles = profiles;

            var presets = new List<ChoiceViewModel>();
            foreach (var preset in ByokSettings.Presets)
            {
                var captured = preset;
                var chip = FindChoice(_presets, preset.Id) ?? new ChoiceViewModel(preset.Id, string.Empty, null, true, delegate { _c.ApplyPreset(captured.Id); }, "preset-" + preset.Id);
                chip.Update(preset.Label.ToUpperInvariant(), null, profile.Preset == preset.Id);
                presets.Add(chip);
            }
            Presets = presets;

            bool needsKey = ByokSettings.PresetFor(profile.Preset).NeedsKey;
            KeyPlaceholder = needsKey ? "paste a key to set or replace it" : "not needed for local models";
            KeyState = KeyStateOf(profile);
            CanDelete = byok.Profiles.Count > 1;
            PaintSpend();

            string signature = Signature(byok);
            if (!_saving && signature != _seen)
            {
                _seen = signature;
                Load(profile);
                Key = string.Empty;
                Show(string.Empty, null);
            }
        }

        private static ChoiceViewModel FindChoice(List<ChoiceViewModel> list, string key)
        {
            foreach (var c in list) { if (c.Key == key) { return c; } }
            return null;
        }

        private static string Signature(ByokSettings byok)
        {
            var p = byok.Active;
            return string.Join("\u001f", new[]
            {
                byok.ActiveIndex.ToString(CultureInfo.InvariantCulture), p.Preset, p.Name, p.BaseUrl, p.Model,
                p.Temperature.ToString("R", CultureInfo.InvariantCulture), p.MaxTokens.ToString(CultureInfo.InvariantCulture),
                p.ReasoningEffort ?? string.Empty, p.DisableStreaming ? "1" : "0", p.SinglePass ? "1" : "0",
                Price(p.PriceInPerM), Price(p.PriceCachedPerM), Price(p.PriceOutPerM),
            });
        }

        private static string Price(float perMillion)
        {
            return perMillion > 0 ? perMillion.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static float ParsePrice(string text, float keep)
        {
            string trimmed = (text ?? string.Empty).Trim().TrimStart('$');
            if (trimmed.Length == 0) { return 0; }
            float value;
            return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 ? value : keep;
        }

        private void PaintSpend()
        {
            var store = _s.Driver != null ? _s.Driver.CampaignUsage : null;
            if (!_s.HasCampaign) { Spend = "Open a campaign to see what it has cost."; return; }
            if (store == null) { Spend = "Cost is not tracked in this run."; return; }
            TokenUsage total = store.Total(_s.CampaignSlug);
            Spend = total.IsEmpty ? "This campaign has no counted model calls yet."
                : "This campaign so far: " + total.CostText() + " (" + total + ", " + total.Calls + " calls).";
        }

        private void Load(ProviderProfile profile)
        {
            PriceIn = Price(profile.PriceInPerM);
            PriceCached = Price(profile.PriceCachedPerM);
            PriceOut = Price(profile.PriceOutPerM);
            Name = profile.Name;
            BaseUrl = profile.BaseUrl;
            Model = profile.Model;
            Temperature = profile.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
            MaxTokens = profile.MaxTokens > 0 ? profile.MaxTokens.ToString(CultureInfo.InvariantCulture) : string.Empty;
            Effort = profile.ReasoningEffort ?? string.Empty;
            PaintEfforts();
            Stream = !profile.DisableStreaming;
            Storyteller = !profile.SinglePass;
        }

        private void PaintEfforts()
        {
            for (int i = 0; i < _efforts.Count; i++) { _efforts[i].Update(EffortLabels[i], null, EffortValues[i] == _effort); }
        }

        private ProviderProfile Edited()
        {
            var profile = _s.Byok.Active;
            float t;
            int m;
            string tokens = _maxTokens.Trim();
            return new ProviderProfile
            {
                Name = _name,
                BaseUrl = _baseUrl,
                Model = _model,
                Temperature = float.TryParse(_temperature.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out t) ? t : profile.Temperature,
                MaxTokens = tokens.Length == 0 ? 0 : int.TryParse(tokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out m) ? m : profile.MaxTokens,
                ReasoningEffort = _effort,
                DisableStreaming = !_stream,
                SinglePass = !_storyteller,
                PriceInPerM = ParsePrice(_priceIn, profile.PriceInPerM),
                PriceCachedPerM = ParsePrice(_priceCached, profile.PriceCachedPerM),
                PriceOutPerM = ParsePrice(_priceOut, profile.PriceOutPerM),
            };
        }

        /// <summary>Saves the edited fields; true when the provider is then ready to play.</summary>
        private bool Persist(out string reason)
        {
            _saving = true;
            try { return _c.SaveProfile(Edited(), _key, out reason); }
            finally { _saving = false; }
        }

        private void DoSave()
        {
            string reason;
            bool ready = Persist(out reason);
            _seen = Signature(_s.Byok);
            Load(_s.Byok.Active);
            Key = string.Empty;
            Refresh();
            Show(ready ? "Saved. This is the active Dungeon Master." : "Saved, but not ready: " + reason, ready);
        }

        private void DoTest()
        {
            string reason;
            Persist(out reason);
            _seen = Signature(_s.Byok);
            Key = string.Empty;
            Refresh();
            Show("Testing…", null);
            _c.Run(_c.TestProvider(delegate (bool okay, string message) { Show(message, okay); }));
        }

        private void DoForget()
        {
            _saving = true;
            try { _c.ForgetProviderKey(); }
            finally { _saving = false; }
            Refresh();
            Show("API key removed from this profile and from disk.", true);
        }

        private void Show(string message, bool? ok)
        {
            Status = DisplayText.Plain(message);
            StatusTone = ok == true ? "leaf" : ok == false ? "blood" : "muted";
        }

        private static string KeyStateOf(ProviderProfile profile)
        {
            string key = profile.ApiKey ?? string.Empty;
            if (key.Length == 0) { return "No key saved."; }
            string tail = key.Length > 4 ? key.Substring(key.Length - 4) : string.Empty;
            return "Key saved (ends …" + tail + "). Paste a new one to replace it.";
        }
    }
}
