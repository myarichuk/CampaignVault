using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Hover tooltips for runtime panels (VisualElement.tooltip only shows in
    /// the Editor). One shared bubble per panel, placed by the pointer and
    /// clamped to the screen.
    /// </summary>
    public static class TooltipLayer
    {
        private const long DelayMs = 450;
        private static Label _bubble;
        private static IVisualElementScheduledItem _pending;

        public static void Install(VisualElement layer)
        {
            _bubble = new Label();
            _bubble.AddToClassList("cv-tooltip");
            _bubble.AddToClassList("cv-tooltip--hidden");
            _bubble.pickingMode = PickingMode.Ignore;
            _bubble.enableRichText = false;
            layer.Add(_bubble);
        }

        public static void Attach(VisualElement target, string text)
        {
            Attach(target, delegate { return text; });
        }

        /// <summary>Text read when the pointer arrives, for tooltips that describe live state.</summary>
        public static void Attach(VisualElement target, Func<string> text)
        {
            target.RegisterCallback<PointerEnterEvent>(delegate (PointerEnterEvent e) { Show(target, text(), e.position); });
            target.RegisterCallback<PointerLeaveEvent>(delegate { Hide(); });
            target.RegisterCallback<PointerDownEvent>(delegate { Hide(); });
        }

        private static void Show(VisualElement target, string text, Vector2 position)
        {
            if (_bubble == null || string.IsNullOrEmpty(text)) { return; }
            if (_pending != null) { _pending.Pause(); }
            _pending = _bubble.schedule.Execute(() =>
            {
                if (target.panel == null) { return; }
                _bubble.text = text;
                var layer = _bubble.parent;
                Vector2 local = layer.WorldToLocal(position);
                float x = Mathf.Min(local.x + 16f, layer.layout.width - 380f);
                float y = local.y + 22f;
                if (y > layer.layout.height - 80f) { y = local.y - 60f; }
                _bubble.style.left = Mathf.Max(8f, x);
                _bubble.style.top = Mathf.Max(8f, y);
                _bubble.BringToFront();
                _bubble.RemoveFromClassList("cv-tooltip--hidden");
            }).StartingIn(DelayMs);
        }

        public static void Hide()
        {
            if (_pending != null) { _pending.Pause(); _pending = null; }
            if (_bubble != null) { _bubble.AddToClassList("cv-tooltip--hidden"); }
        }
    }

    /// <summary>Transient notices, bottom left: slide in, linger, fade out. Errors linger longer.</summary>
    public sealed class ToastHost
    {
        private const int MaxToasts = 4;
        private readonly VisualElement _host;

        public ToastHost(VisualElement host, VaultAppState state)
        {
            _host = host;
            _host.pickingMode = PickingMode.Ignore;
            state.Toast += Show;
        }

        public void Show(string message, ToastKind kind)
        {
            var toast = Ui.El("cv-toast cv-toast--" + kind.ToString().ToLowerInvariant());
            string icon = kind == ToastKind.Error || kind == ToastKind.Warning ? "warning" : kind == ToastKind.Success ? "check" : "spark";
            string tint = kind == ToastKind.Error ? "blood" : kind == ToastKind.Success ? "leaf" : "gold";
            toast.Add(Ui.Icon(icon, "cv-icon--" + tint));
            toast.Add(Ui.Text(message, "cv-toast__text"));
            toast.RegisterCallback<ClickEvent>(delegate { Dismiss(toast); });
            _host.Add(toast);
            Ui.Enter(toast, "cv-toast--enter", true);
            while (_host.childCount > MaxToasts) { _host.RemoveAt(0); }
            long linger = kind == ToastKind.Error ? 8000 : kind == ToastKind.Warning ? 6000 : 4200;
            toast.schedule.Execute(() => { Dismiss(toast); }).StartingIn(linger);
        }

        private static void Dismiss(VisualElement toast)
        {
            if (toast.parent == null || toast.ClassListContains("cv-toast--leave")) { return; }
            toast.AddToClassList("cv-toast--leave");
            toast.schedule.Execute(() => { toast.RemoveFromHierarchy(); }).StartingIn(300);
        }
    }

    /// <summary>A full-screen page (Campaigns, Settings, Character…) shown over the table.</summary>
    public abstract class Overlay
    {
        public VisualElement Root { get; private set; }
        protected VisualElement Body { get; private set; }
        /// <summary>Fixed under the title, above the scrolling body (tabs, page actions).</summary>
        protected VisualElement Toolbar { get; private set; }
        protected VisualElement Foot { get; private set; }
        protected OverlayHost Host { get; private set; }

        /// <summary>Title in the header; narrow = a dialog instead of a page.</summary>
        protected abstract string Title { get; }
        protected virtual string TitleIcon { get { return null; } }
        protected virtual bool Narrow { get { return false; } }
        /// <summary>An extra class on the modal for pages with their own size (the character sheet).</summary>
        protected virtual string ModalClass { get { return null; } }
        /// <summary>False for dialogs that must be completed (first-run setup without a provider).</summary>
        public virtual bool CanDismiss { get { return true; } }

        internal void Build(OverlayHost host)
        {
            Host = host;
            Root = Ui.El("cv-overlay cv-overlay--hidden");
            var modal = Ui.Frame(Ui.El("cv-modal" + (Narrow ? " cv-modal--narrow" : string.Empty) + (ModalClass != null ? " " + ModalClass : string.Empty)));
            modal.RegisterCallback<ClickEvent>(delegate (ClickEvent e) { e.StopPropagation(); });
            var head = Ui.El("cv-modal__head");
            if (TitleIcon != null) { head.Add(Ui.Icon(TitleIcon, "cv-icon--lg cv-icon--gold cv-modal__icon")); }
            head.Add(Ui.Text(Title.ToUpperInvariant(), "cv-h1 cv-modal__title"));
            if (CanDismiss) { head.Add(Ui.IconButton("close", "Close (Esc)", "cv-btn--ghost", delegate { host.Close(this); })); }
            modal.Add(head);
            Toolbar = Ui.El("cv-modal__toolbar");
            modal.Add(Toolbar);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("cv-modal__scroll");
            scroll.style.flexGrow = 1;
            Body = scroll.contentContainer;
            modal.Add(scroll);
            Foot = Ui.El("cv-modal__foot");
            modal.Add(Foot);
            Root.Add(modal);
            Root.RegisterCallback<ClickEvent>(delegate { if (CanDismiss) { host.Close(this); } });
            BuildContent();
        }

        protected abstract void BuildContent();

        /// <summary>Called each time the overlay opens (refresh data here).</summary>
        public virtual void OnOpen() { }

        /// <summary>Called when it closes; unsubscribe from state here.</summary>
        public virtual void OnClose() { }

        protected void Close() { Host.Close(this); }
    }

    /// <summary>The overlay stack. Esc closes the top one only.</summary>
    public sealed class OverlayHost
    {
        private readonly VisualElement _layer;
        private readonly List<Overlay> _stack = new List<Overlay>();

        public OverlayHost(VisualElement layer) { _layer = layer; }

        /// <summary>The last page closed: the table has the player's attention again.</summary>
        public event Action AllClosed;

        public bool AnyOpen { get { return _stack.Count > 0; } }
        public Overlay Top { get { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; } }

        public T Open<T>(T overlay) where T : Overlay
        {
            if (_stack.Contains(overlay)) { return overlay; }
            if (overlay.Root == null) { overlay.Build(this); }
            _layer.Add(overlay.Root);
            _stack.Add(overlay);
            overlay.OnOpen();
            var root = overlay.Root;
            root.schedule.Execute(() => { root.RemoveFromClassList("cv-overlay--hidden"); }).StartingIn(16);
            return overlay;
        }

        public bool IsOpen(Overlay overlay) { return _stack.Contains(overlay); }

        public void Close(Overlay overlay)
        {
            if (!_stack.Remove(overlay)) { return; }
            overlay.OnClose();
            TooltipLayer.Hide();
            var root = overlay.Root;
            root.AddToClassList("cv-overlay--hidden");
            root.schedule.Execute(() => { if (!_stack.Contains(overlay)) { root.RemoveFromHierarchy(); } }).StartingIn(220);
            if (_stack.Count == 0 && AllClosed != null) { AllClosed(); }
        }

        public bool CloseTop()
        {
            var top = Top;
            if (top == null || !top.CanDismiss) { return false; }
            Close(top);
            return true;
        }

        public void Toggle(Overlay overlay)
        {
            if (IsOpen(overlay)) { Close(overlay); } else { Open(overlay); }
        }
    }
}
