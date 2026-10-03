using System;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Dialogs
{
    /// <summary>A yes/no question (Templates/Dialogs/Confirm.uxml).</summary>
    public sealed class ConfirmViewModel : ViewModel
    {
        public ConfirmViewModel(string message, string confirmLabel, bool danger, Action cancel, Action confirm)
        {
            Message = DisplayText.Plain(message);
            ConfirmLabel = confirmLabel;
            Danger = danger;
            Cancel = cancel;
            Confirm = confirm;
        }

        [CreateProperty] public string Message { get; private set; }
        [CreateProperty] public string ConfirmLabel { get; private set; }
        [CreateProperty] public bool Danger { get; private set; }
        [CreateProperty] public bool Primary { get { return !Danger; } }
        [CreateProperty] public Action Cancel { get; private set; }
        [CreateProperty] public Action Confirm { get; private set; }
    }

    /// <summary>F12 (Templates/Dialogs/Inspector.uxml): what the driver last sent and got back, token usage, and its tool log.</summary>
    public sealed class InspectorViewModel : ViewModel
    {
        private readonly VaultAppState _s;
        private string _request = string.Empty;
        private string _usage = string.Empty;
        private string _response = string.Empty;
        private string _tools = string.Empty;
        private string _failure = string.Empty;

        public InspectorViewModel(VaultAppState state, VaultController controller)
        {
            _s = state;
            CopyResponse = delegate { Copy(_s.Driver.LastResponseJson, "Response copied."); };
            CopyFailure = delegate { Copy(_failure, "Failure details copied."); };
            CopyTools = delegate { Copy(string.Join("\n", _s.Driver.ToolLog.ToArray()), "Tool log copied."); };
            CopyPrompt = delegate { Copy(_s.Prompts.BuildSystemPrompt(), "System prompt copied."); };
            Export = delegate
            {
                string path = controller.ExportTranscript();
                if (path == null) { return; }
                GUIUtility.systemCopyBuffer = path;
                _s.RaiseToast("Transcript saved (path copied): " + path, ToastKind.Success);
            };
            Watch(state, StateArea.Driver);
        }

        [CreateProperty] public string Request { get { return _request; } private set { Set(ref _request, value); } }
        [CreateProperty] public string Usage { get { return _usage; } private set { Set(ref _usage, value); } }
        [CreateProperty] public string Response { get { return _response; } private set { Set(ref _response, value); } }
        [CreateProperty] public string Tools { get { return _tools; } private set { Set(ref _tools, value); } }
        /// <summary>The last failed model call, as the copy-pastable technical block (the key is never in it).</summary>
        [CreateProperty] public string Failure { get { return _failure; } private set { Set(ref _failure, value); } }
        [CreateProperty] public Action CopyFailure { get; private set; }
        [CreateProperty] public Action CopyResponse { get; private set; }
        [CreateProperty] public Action CopyTools { get; private set; }
        [CreateProperty] public Action CopyPrompt { get; private set; }
        [CreateProperty] public Action Export { get; private set; }

        private void Copy(string text, string toast)
        {
            GUIUtility.systemCopyBuffer = text;
            _s.RaiseToast(toast, ToastKind.Success);
        }

        public override void Refresh()
        {
            var d = _s.Driver;
            Request = string.IsNullOrEmpty(d.LastRequestMeta) ? "(no request sent yet)" : d.LastRequestMeta;
            Usage = DescribeUsage(d);
            Failure = d.LastFailure == null ? "(no failure)" : d.LastFailure.Technical;
            Response = string.IsNullOrEmpty(d.LastResponseJson) ? "(no response yet)" : d.LastResponseJson;
            Tools = d.ToolLog.Count == 0 ? "(none yet)" : string.Join("\n", d.ToolLog.ToArray());
        }

        public static string DescribeUsage(OpenAiChatDriver d)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Session: ").Append(d.SessionUsage).Append(" · ").Append(d.Turns.Count).Append(" turns");
            if (d.Turns.Count > 0 && !d.SessionUsage.IsEmpty)
            {
                sb.Append(" · avg ").Append(d.SessionUsage.Prompt / d.Turns.Count).Append(" in / ")
                  .Append(d.SessionUsage.Completion / d.Turns.Count).Append(" out per turn");
            }
            int from = Math.Max(0, d.Turns.Count - 5);
            for (int i = d.Turns.Count - 1; i >= from; i--)
            {
                var t = d.Turns[i];
                sb.Append("\nTurn ").Append(i + 1).Append(": ").Append(t.Usage)
                  .Append(" · ").Append(t.Tools.Count).Append(" tools · ")
                  .Append(t.Narration.Length).Append(" chars of narration");
            }
            return sb.ToString();
        }
    }
}
