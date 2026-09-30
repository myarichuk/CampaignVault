using System;
using System.Globalization;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The AI provider editor (profiles, preset, endpoint, model, sampling,
    /// streaming, key). Settings and first-run Setup each embed one; both
    /// repaint from StateArea.Providers, so neither ever edits a stale profile.
    /// </summary>
    public sealed class ProviderForm
    {
        private static readonly string[] EffortValues = { string.Empty, "low", "medium", "high" };
        private static readonly string[] EffortLabels = { "DEFAULT", "LOW", "MEDIUM", "HIGH" };

        private readonly VisualElement _host;
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _onChanged;
        private Label _status;
        private bool _saving;

        public ProviderForm(VisualElement host, VaultAppState state, VaultController controller, Action onChanged)
        {
            _host = host;
            _state = state;
            _controller = controller;
            _onChanged = onChanged;
            Render(null, null);
        }

        /// <summary>Views call this on StateArea.Providers from elsewhere.</summary>
        public void Refresh()
        {
            if (!_saving) { Render(null, null); }
        }

        private void Render(string message, bool? ok)
        {
            _host.Clear();
            var byok = _state.Byok;
            var profile = byok.Active;

            _host.Add(Ui.Text("PROFILES", "cv-caption"));
            var profiles = Ui.El("cv-row");
            profiles.style.flexWrap = Wrap.Wrap;
            profiles.style.marginBottom = 12;
            for (int i = 0; i < byok.Profiles.Count; i++)
            {
                int index = i;
                bool active = i == byok.ActiveIndex;
                var b = Ui.Button(byok.Profiles[i].Name, active ? "check" : null, "cv-btn--small" + (active ? " cv-btn--selected" : string.Empty), delegate { _controller.SelectProfile(index); });
                b.style.marginRight = 6;
                b.style.marginBottom = 6;
                profiles.Add(b);
            }
            var add = Ui.Button("NEW", "add", "cv-btn--small cv-btn--ghost", delegate { _controller.AddProfile("openai"); });
            profiles.Add(add);
            _host.Add(profiles);

            _host.Add(Ui.Text("PRESET", "cv-caption"));
            var presets = Ui.El("cv-row");
            presets.style.flexWrap = Wrap.Wrap;
            presets.style.marginBottom = 12;
            foreach (var preset in ByokSettings.Presets)
            {
                var captured = preset;
                var b = Ui.Button(preset.Label.ToUpperInvariant(), null, "cv-btn--small" + (profile.Preset == preset.Id ? " cv-btn--selected" : string.Empty), delegate { _controller.ApplyPreset(captured.Id); });
                b.style.marginRight = 6;
                b.style.marginBottom = 6;
                presets.Add(b);
            }
            _host.Add(presets);

            var name = Ui.LabeledField(_host, "Profile name", "Profile name", profile.Name);
            var baseUrl = Ui.LabeledField(_host, "Endpoint (OpenAI-compatible base URL)", "https://api.openai.com/v1", profile.BaseUrl);
            var model = Ui.LabeledField(_host, "Model", "model id", profile.Model);

            var tuning = Ui.El("cv-row");
            var tempCol = Ui.El("cv-grow");
            tempCol.style.marginRight = 12;
            var temp = Ui.LabeledField(tempCol, "Temperature 0–2", "1", profile.Temperature.ToString("0.##", CultureInfo.InvariantCulture));
            var maxCol = Ui.El("cv-grow");
            var maxTokens = Ui.LabeledField(maxCol, "Max reply tokens", "provider default", profile.MaxTokens > 0 ? profile.MaxTokens.ToString(CultureInfo.InvariantCulture) : string.Empty);
            tuning.Add(tempCol);
            tuning.Add(maxCol);
            _host.Add(tuning);

            _host.Add(Ui.Text("REASONING EFFORT (OPENROUTER ONLY)", "cv-caption"));
            string effort = profile.ReasoningEffort ?? string.Empty;
            var efforts = Ui.El("cv-row");
            efforts.style.marginBottom = 12;
            var effortButtons = new Button[EffortValues.Length];
            for (int i = 0; i < EffortValues.Length; i++)
            {
                string value = EffortValues[i];
                var b = Ui.Button(EffortLabels[i], null, "cv-btn--small" + (effort == value ? " cv-btn--selected" : string.Empty), delegate
                {
                    // Select in place: re-rendering would drop whatever was typed but not saved.
                    effort = value;
                    for (int j = 0; j < effortButtons.Length; j++) { effortButtons[j].EnableInClassList("cv-btn--selected", EffortValues[j] == value); }
                });
                b.style.marginRight = 6;
                effortButtons[i] = b;
                efforts.Add(b);
            }
            _host.Add(efforts);

            bool stream = !profile.DisableStreaming;
            Ui.Switch(_host, "Stream replies as they're written", stream, delegate (bool on) { stream = on; });

            bool storyteller = !profile.SinglePass;
            Ui.Switch(_host, "Separate storyteller pass (the scene is written by a call that sees no tool data)", storyteller, delegate (bool on) { storyteller = on; });

            bool needsKey = ByokSettings.PresetFor(profile.Preset).NeedsKey;
            _host.Add(Ui.Text("API KEY", "cv-caption"));
            var key = Ui.Password(needsKey ? "paste a key to set or replace it" : "not needed for local models");
            _host.Add(key);
            _host.Add(Ui.Text(KeyState(profile), "cv-muted cv-body"));

            _status = Ui.Text(string.Empty, "cv-body");
            _status.style.marginTop = 10;
            _host.Add(_status);
            if (message != null) { Show(message, ok); }

            var actions = Ui.El("cv-row");
            actions.style.marginTop = 14;
            actions.style.flexWrap = Wrap.Wrap;
            Func<ProviderProfile> edited = delegate
            {
                float t;
                int m;
                return new ProviderProfile
                {
                    Name = name.value,
                    BaseUrl = baseUrl.value,
                    Model = model.value,
                    Temperature = float.TryParse(temp.value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out t) ? t : profile.Temperature,
                    MaxTokens = maxTokens.value.Trim().Length == 0 ? 0 : int.TryParse(maxTokens.value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out m) ? m : profile.MaxTokens,
                    ReasoningEffort = effort,
                    DisableStreaming = !stream,
                    SinglePass = !storyteller,
                };
            };
            var save = Ui.Button("SAVE PROFILE", "check", "cv-btn--primary", delegate
            {
                string reason;
                _saving = true;
                bool ready = _controller.SaveProfile(edited(), key.value, out reason);
                _saving = false;
                Render(ready ? "Saved. This is the active Dungeon Master." : "Saved, but not ready: " + reason, ready);
                if (_onChanged != null) { _onChanged(); }
            });
            save.style.marginRight = 8;
            actions.Add(save);
            var test = Ui.Button("TEST CONNECTION", "spark", null, delegate
            {
                string reason;
                _saving = true;
                _controller.SaveProfile(edited(), key.value, out reason);
                _saving = false;
                key.value = string.Empty;
                Show("Testing…", null);
                _controller.Run(_controller.TestProvider(delegate (bool okay, string msg) { Show(msg, okay); }));
            });
            test.style.marginRight = 8;
            actions.Add(test);
            var forget = Ui.Button("FORGET KEY", "key", "cv-btn--ghost", delegate
            {
                _saving = true;
                _controller.ForgetProviderKey();
                _saving = false;
                Render("API key removed from this profile and from disk.", true);
            });
            forget.style.marginRight = 8;
            actions.Add(forget);
            if (byok.Profiles.Count > 1)
            {
                actions.Add(Ui.Button("DELETE PROFILE", "trash", "cv-btn--danger", delegate { _controller.DeleteProfile(); }));
            }
            _host.Add(actions);
            var note = Ui.Text("Keys are stored as plain text in " + ByokSettings.FilePath + " on this device and are only ever sent to the endpoint above.", "cv-muted cv-body");
            note.style.fontSize = 13;
            note.style.marginTop = 12;
            _host.Add(note);
        }

        private void Show(string message, bool? ok)
        {
            if (_status == null) { return; }
            Ui.SetText(_status, message);
            _status.EnableInClassList("cv-text-leaf", ok == true);
            _status.EnableInClassList("cv-text-blood", ok == false);
            _status.EnableInClassList("cv-muted", ok == null);
        }

        private static string KeyState(ProviderProfile profile)
        {
            string key = profile.ApiKey ?? string.Empty;
            if (key.Length == 0) { return "No key saved."; }
            string tail = key.Length > 4 ? key.Substring(key.Length - 4) : string.Empty;
            return "Key saved (ends …" + tail + "). Paste a new one to replace it.";
        }
    }
}
