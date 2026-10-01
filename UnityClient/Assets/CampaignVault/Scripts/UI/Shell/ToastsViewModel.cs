using System;
using System.Collections;
using System.Collections.ObjectModel;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Shell
{
    /// <summary>One transient notice (Templates/Shell/Toast.uxml): slides in, lingers, fades out; a click dismisses it.</summary>
    public sealed class ToastViewModel : ViewModel
    {
        private bool _entering;
        private bool _leaving;

        public ToastViewModel(string message, ToastKind kind, Action<ToastViewModel> dismiss)
        {
            Message = DisplayText.Plain(message);
            Kind = kind.ToString().ToLowerInvariant();
            Icon = kind == ToastKind.Error || kind == ToastKind.Warning ? "warning" : kind == ToastKind.Success ? "check" : "spark";
            Dismiss = delegate { dismiss(this); };
        }

        [CreateProperty] public string Message { get; private set; }
        /// <summary>"info", "success", "warning" or "error": the toast's colour is a USS class per kind.</summary>
        [CreateProperty] public string Kind { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
        /// <summary>True for the first moments, so the slide-in transition has a state to start from.</summary>
        [CreateProperty] public bool Entering { get { return _entering; } set { Set(ref _entering, value); } }
        [CreateProperty] public bool Leaving { get { return _leaving; } set { Set(ref _leaving, value); } }
        [CreateProperty] public Action Dismiss { get; private set; }
    }

    /// <summary>Transient notices, bottom left. Errors linger longer. The list is incremental (a <see cref="Controls.LiveRepeater"/>), so a fading toast is never re-bound to another.</summary>
    public sealed class ToastsViewModel : ViewModel
    {
        public const int MaxToasts = 4;
        private const long FadeMs = 300;

        private readonly VaultAppState _s;
        private readonly Later _later;
        private readonly ObservableCollection<ToastViewModel> _toasts = new ObservableCollection<ToastViewModel>();

        public ToastsViewModel(VaultAppState state, Later later)
        {
            _s = state;
            _later = later;
            state.Toast += Show;
        }

        [CreateProperty] public IList Toasts { get { return _toasts; } }

        public ObservableCollection<ToastViewModel> Items { get { return _toasts; } }

        public void Show(string message, ToastKind kind)
        {
            var toast = new ToastViewModel(message, kind, Dismiss);
            toast.Entering = true;
            _toasts.Add(toast);
            _later(delegate { toast.Entering = false; }, 20);
            while (_toasts.Count > MaxToasts) { _toasts.RemoveAt(0); }
            long linger = kind == ToastKind.Error ? 8000 : kind == ToastKind.Warning ? 6000 : 4200;
            _later(delegate { Dismiss(toast); }, linger);
        }

        private void Dismiss(ToastViewModel toast)
        {
            if (toast.Leaving || !_toasts.Contains(toast)) { return; }
            toast.Leaving = true;
            _later(delegate { _toasts.Remove(toast); }, FadeMs);
        }

        public override void Dispose()
        {
            _s.Toast -= Show;
            base.Dispose();
        }
    }
}
