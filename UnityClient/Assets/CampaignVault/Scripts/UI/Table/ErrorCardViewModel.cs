using System;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Table
{
    /// <summary>
    /// A failed model call, shown the same way everywhere (the story, the onboarding chat, the builder): the friendly
    /// line, a Details fold-out with the copy-pastable technical block, and either RETRY (the same request again) or
    /// OPEN SETTINGS when only a change in Settings can help.
    /// </summary>
    public sealed class ErrorCardViewModel : StoryItemViewModel
    {
        private readonly Func<bool> _canRetry;
        private bool _open;
        private bool _showRetry;

        /// <param name="retry">Sends the identical request again. Null hides the RETRY button.</param>
        /// <param name="openSettings">Opens the provider settings (offered when only a change there can help).</param>
        /// <param name="copied">Told once the details are on the clipboard (a toast).</param>
        /// <param name="canRetry">Whether the request can still be sent again (the story's last line, say); null means always.</param>
        /// <param name="watch">When given, RETRY is re-checked on every driver change.</param>
        public ErrorCardViewModel(LlmFailure failure, Action retry, Action openSettings, Action<string> copied, Func<bool> canRetry = null, VaultAppState watch = null)
        {
            Failure = failure;
            _canRetry = canRetry;
            Friendly = DisplayText.Plain(failure.Friendly);
            Technical = failure.Technical;
            HasRetry = retry != null && failure.Retryable;
            ShowSettings = failure.NeedsSettings && openSettings != null;
            Toggle = delegate { Open = !Open; };
            Copy = delegate
            {
                GUIUtility.systemCopyBuffer = Technical;
                if (copied != null) { copied("Error details copied."); }
            };
            Retry = delegate { if (retry != null) { retry(); } };
            OpenSettings = delegate { if (openSettings != null) { openSettings(); } };
            if (watch != null) { Watch(watch, StateArea.Driver | StateArea.Busy); }
            Refresh();
        }

        public LlmFailure Failure { get; private set; }
        public bool HasRetry { get; private set; }
        [CreateProperty] public string Friendly { get; private set; }
        [CreateProperty] public string Technical { get; private set; }
        [CreateProperty] public bool ShowSettings { get; private set; }
        [CreateProperty] public bool Open { get { return _open; } private set { Set(ref _open, value); } }
        /// <summary>RETRY is offered while the request can still be resent; an older card's request has moved on.</summary>
        [CreateProperty] public bool ShowRetry { get { return _showRetry; } private set { Set(ref _showRetry, value); } }
        [CreateProperty] public Action Toggle { get; private set; }
        [CreateProperty] public Action Copy { get; private set; }
        [CreateProperty] public Action Retry { get; private set; }
        [CreateProperty] public Action OpenSettings { get; private set; }

        public override string Template { get { return "Common/ErrorCard"; } }

        public override void Refresh()
        {
            ShowRetry = HasRetry && (_canRetry == null || _canRetry());
        }
    }
}

namespace CampaignVault.UnityClient.UI.Table
{
    /// <summary>Keeps one card for one recorded failure, so a repaint doesn't rebuild it (and fold its details shut).</summary>
    public sealed class ErrorCardSlot
    {
        private FailedCall _call;
        private ErrorCardViewModel _card;

        /// <summary>The card for <paramref name="call"/> while the screen still shows its error text, else null.</summary>
        public ErrorCardViewModel Sync(FailedCall call, string shownError)
        {
            if (call == null || call.Failure == null || string.IsNullOrEmpty(shownError) || call.Message != shownError)
            {
                Drop();
                return null;
            }
            if (!ReferenceEquals(call, _call))
            {
                Drop();
                _call = call;
                _card = new ErrorCardViewModel(call.Failure, call.Retry, call.OpenSettings, call.Copied);
            }
            return _card;
        }

        private void Drop()
        {
            if (_card != null) { _card.Dispose(); }
            _card = null;
            _call = null;
        }
    }
}
