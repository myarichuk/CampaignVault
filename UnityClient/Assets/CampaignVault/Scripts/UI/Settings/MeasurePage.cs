#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Text;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Measure;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>
    /// Settings → MEASURE (debug and editor builds only): plays the eight-beat script on a scratch world with the active provider,
    /// once as the client plays (a tool loop, then a separate storyteller) and once as a web connector would (one model, tools and
    /// scene together), and compares the scenes (NARRATION_LENGTH_PLAN.md, L3). It never touches the player's campaigns.
    /// </summary>
    public sealed class MeasurePage : SettingsPage
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private MeasureRig _rig;
        private bool _busy;
        private bool _remedies = true;
        private int _repeats = 1;
        private string _status = "Idle.";
        private string _result = string.Empty;
        private string _report = string.Empty;
        private readonly Dictionary<MeasureMode, MeasureRun> _runs = new Dictionary<MeasureMode, MeasureRun>();

        public MeasurePage(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            RunStoryteller = delegate { Start(new[] { MeasureMode.Storyteller }); };
            RunConnector = delegate { Start(new[] { MeasureMode.Connector }); };
            RunBoth = delegate { Start(new[] { MeasureMode.Storyteller, MeasureMode.Connector }); };
            Stop = delegate { if (_rig != null) { _rig.Cancel(); Status = "Stopping after the current call…"; } };
            CyclePasses = delegate { Repeats = _repeats % MeasureRig.MaxRepeats + 1; };
            ClearFiles = delegate { if (!_busy) { MeasureRig.ClearFiles(); Status = "Removed the scratch server copy and the saved reports."; } };
            Watch(state, StateArea.Providers);
            Refresh();
        }

        public override string Template { get { return "Settings/MeasurePage"; } }

        [CreateProperty] public string Provider { get; private set; }
        [CreateProperty] public string Warning { get; private set; }
        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        [CreateProperty] public string Result { get { return _result; } private set { Set(ref _result, value); } }
        [CreateProperty] public string ReportPath { get { return _report; } private set { Set(ref _report, value); } }
        [CreateProperty] public bool Busy { get { return _busy; } private set { if (Set(ref _busy, value)) { Notify("Idle"); } } }
        [CreateProperty] public bool Idle { get { return !_busy; } }

        /// <summary>Off records a baseline: the storyteller's length target, pinned exemplar and passage filter are left out.</summary>
        [CreateProperty] public bool Remedies { get { return _remedies; } set { Set(ref _remedies, value); } }

        public int Repeats
        {
            get { return _repeats; }
            private set { if (Set(ref _repeats, value)) { Notify("PassesLabel"); } }
        }

        [CreateProperty] public string PassesLabel { get { return "PASSES PER MODE: " + _repeats; } }
        [CreateProperty] public Action RunStoryteller { get; private set; }
        [CreateProperty] public Action RunConnector { get; private set; }
        [CreateProperty] public Action RunBoth { get; private set; }
        [CreateProperty] public Action Stop { get; private set; }
        [CreateProperty] public Action CyclePasses { get; private set; }
        [CreateProperty] public Action ClearFiles { get; private set; }

        public override void Refresh()
        {
            var p = _s.Byok.Active;
            Provider = DisplayText.Plain(p.Name + " · " + p.Model + " · " + MeasureRig.DescribeSettings(p));
            Warning = "Real model calls: one pass is about eight turns and costs what eight turns cost, doubled by tool loops. "
                + "It runs on a scratch campaign and a second server of its own; your campaigns are not touched. "
                + "The first run unpacks a copy of the server (about 1 GB) under " + DisplayText.Plain(MeasureRig.Root) + ".";
            Notify("Provider");
            Notify("Warning");
        }

        private void Start(MeasureMode[] modes)
        {
            if (_busy) { return; }
            _rig = new MeasureRig(_s.Driver, _s.Byok);
            _runs.Clear();
            Result = string.Empty;
            ReportPath = string.Empty;
            Busy = true;
            _c.Run(RunModes(modes));
        }

        private System.Collections.IEnumerator RunModes(MeasureMode[] modes)
        {
            try
            {
                foreach (MeasureMode mode in modes)
                {
                    MeasureRun finished = null;
                    bool over = false;
                    yield return _rig.Run(mode, _repeats, _remedies,
                        delegate (string line) { Status = DisplayText.Plain(line); },
                        delegate { ShowProgress(); },
                        delegate (MeasureRun run) { finished = run; over = true; });
                    if (!over || finished == null) { break; }
                    _runs[mode] = finished;
                    ShowProgress();
                    string path = MeasureRig.ReportsDir;
                    ReportPath = DisplayText.Plain("Reports: " + path);
                }
            }
            finally
            {
                Busy = false;
                _rig = null;
            }
        }

        /// <summary>Called after every beat: the finished runs, and the one in flight, as plain text.</summary>
        private void ShowProgress()
        {
            var sb = new StringBuilder();
            MeasureRun a, b;
            _runs.TryGetValue(MeasureMode.Storyteller, out a);
            _runs.TryGetValue(MeasureMode.Connector, out b);
            if (a != null && b != null) { sb.Append(MeasureReport.Compare(a, b)).Append('\n'); }
            foreach (var pair in _runs)
            {
                sb.Append(pair.Value.Title).Append('\n').Append(MeasureReport.SummaryLines(MeasureReport.Summarize(pair.Value))).Append('\n');
            }
            MeasureRun live = _rig != null ? _rig.Current : null;
            if (live != null && !_runs.ContainsValue(live))
            {
                sb.Append(live.Title).Append(" (running, ").Append(live.Beats.Count).Append(" beats so far)\n")
                  .Append(MeasureReport.SummaryLines(MeasureReport.Summarize(live))).Append('\n');
            }
            Result = DisplayText.Plain(sb.ToString());
        }
    }
}
#endif
