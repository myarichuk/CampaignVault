using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Controls;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Hover tooltips for runtime panels (VisualElement.tooltip only shows in the Editor). Any element's
    /// <c>tooltip</c> (set in UXML, bound, or by <see cref="Attach(VisualElement, string)"/>) shows in one shared
    /// bubble per panel, placed by the pointer and clamped to the screen. Disabled elements get no pointer events of
    /// their own, so a disabled button's reason goes on a wrapper.
    /// </summary>
    public static class TooltipLayer
    {
        private const long DelayMs = 450;
        private static readonly ConditionalWeakTable<VisualElement, Func<string>> Live = new ConditionalWeakTable<VisualElement, Func<string>>();
        private static Label _bubble;
        private static VisualElement _owner;
        private static IVisualElementScheduledItem _pending;

        /// <summary>The bubble lives on layer; hovers are watched on the layer's parent (the whole UI).</summary>
        public static void Install(VisualElement layer)
        {
            _bubble = new Label();
            _bubble.AddToClassList("cv-tooltip");
            _bubble.AddToClassList("cv-tooltip--hidden");
            _bubble.pickingMode = PickingMode.Ignore;
            _bubble.enableRichText = false;
            layer.Add(_bubble);
            var watched = layer.parent ?? layer;
            watched.RegisterCallback<PointerMoveEvent>(OnMove, TrickleDown.TrickleDown);
            watched.RegisterCallback<PointerDownEvent>(delegate { Hide(); }, TrickleDown.TrickleDown);
            watched.RegisterCallback<PointerLeaveEvent>(delegate { Hide(); _owner = null; });
        }

        /// <summary>Code-built elements: the same as tooltip="…" in UXML.</summary>
        public static void Attach(VisualElement target, string text)
        {
            target.tooltip = text;
        }

        /// <summary>Text read when the pointer arrives, for tooltips that describe live state.</summary>
        public static void Attach(VisualElement target, Func<string> text)
        {
            Live.AddOrUpdate(target, text);
        }

        private static void OnMove(PointerMoveEvent e)
        {
            var owner = Owner(e.target as VisualElement);
            if (owner == _owner) { return; }
            Hide();
            _owner = owner;
            if (owner != null) { Show(owner, e.position); }
        }

        private static VisualElement Owner(VisualElement e)
        {
            Func<string> live;
            for (; e != null; e = e.parent)
            {
                if (Live.TryGetValue(e, out live) || !string.IsNullOrEmpty(e.tooltip)) { return e; }
            }
            return null;
        }

        private static string TextOf(VisualElement owner)
        {
            Func<string> live;
            return Live.TryGetValue(owner, out live) ? live() : owner.tooltip;
        }

        private static void Show(VisualElement target, Vector2 position)
        {
            if (_bubble == null) { return; }
            _pending = _bubble.schedule.Execute(() =>
            {
                string text = TextOf(target);
                if (target.panel == null || _owner != target || string.IsNullOrEmpty(text)) { return; }
                _bubble.text = text;
                var layer = _bubble.parent;
                Vector2 local = layer.WorldToLocal(position);
                float x = Mathf.Min(local.x + 16f, layer.layout.width - 380f);
                float y = local.y + 22f;
                if (y > layer.layout.height - 80f) { y = local.y - 60f; }
                // Pointer-relative placement: a runtime value, the one kind of inline style allowed.
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

    /// <summary>
    /// A full-screen page (Campaigns, Settings, Character…) shown over the table, in the Shell/Modal.uxml frame.
    /// A page with a <see cref="Template"/> fills the modal's regions from it (the children of its top-level
    /// elements named toolbar, body, dock, foot) and binds to the view model <see cref="CreateViewModel"/> makes each time it opens.
    /// </summary>
    public abstract class Overlay
    {
        public VisualElement Root { get; private set; }
        protected VisualElement Body { get; private set; }
        /// <summary>Fixed under the title, above the scrolling body (tabs, page actions).</summary>
        protected VisualElement Toolbar { get; private set; }
        /// <summary>Fixed between the scrolling body and the foot (a chat composer).</summary>
        protected VisualElement Dock { get; private set; }
        protected VisualElement Foot { get; private set; }
        protected OverlayHost Host { get; private set; }
        /// <summary>What the page's template binds to while it's open; null for pages without a template.</summary>
        protected ViewModel ViewModel { get; private set; }

        private Label _title;
        private VisualElement _modal;

        /// <summary>Title in the header (read on every open); narrow = a dialog instead of a page.</summary>
        protected abstract string Title { get; }
        protected virtual string TitleIcon { get { return null; } }
        protected virtual bool Narrow { get { return false; } }
        /// <summary>An extra class on the modal for pages with their own size (the character sheet).</summary>
        protected virtual string ModalClass { get { return null; } }
        /// <summary>False for dialogs that must be completed (first-run setup without a provider).</summary>
        public virtual bool CanDismiss { get { return true; } }
        /// <summary>The page's layout under Resources/VaultUI/Templates, or null for a page built in code.</summary>
        protected virtual string Template { get { return null; } }

        /// <summary>The page's view model, made on every open and disposed on close.</summary>
        protected virtual ViewModel CreateViewModel() { return null; }

        internal void Build(OverlayHost host)
        {
            Host = host;
            Root = Templates.Clone("Shell/Modal");
            var modal = _modal = Root.Q("modal");
            modal.EnableInClassList("cv-modal--narrow", Narrow);
            if (ModalClass != null) { modal.AddToClassList(ModalClass); }
            modal.RegisterCallback<ClickEvent>(delegate (ClickEvent e) { e.StopPropagation(); });
            var icon = Root.Q("modal-icon");
            if (TitleIcon != null) { icon.AddToClassList("cv-icon--" + TitleIcon); }
            else { icon.AddToClassList("cv-hidden"); }
            _title = Root.Q<Label>("modal-title");
            var close = Root.Q<VaultButton>("modal-close");
            close.command = delegate { host.Close(this); };
            close.EnableInClassList("cv-hidden", !CanDismiss);
            Toolbar = Root.Q("modal-toolbar");
            Body = Root.Q<ScrollView>("modal-scroll").contentContainer;
            Dock = Root.Q("modal-dock");
            Foot = Root.Q("modal-foot");
            Root.RegisterCallback<ClickEvent>(delegate { if (CanDismiss) { host.Close(this); } });
            BuildContent();
        }

        /// <summary>A fresh copy of the page's template on every open, so nothing from the last visit shows for a frame.</summary>
        private void CloneTemplate()
        {
            foreach (var region in new[] { Toolbar, Body, Dock, Foot }) { region.Clear(); }
            var page = new VisualElement();
            Templates.CloneInto(Template, page);
            Slot(page, "toolbar", Toolbar);
            Slot(page, "body", Body);
            Slot(page, "dock", Dock);
            Slot(page, "foot", Foot);
        }

        /// <summary>The children of the template's top-level element with this name move into the region (the element itself is only a carrier, so rules like ".cv-modal__foot > .cv-btn" still match).</summary>
        private static void Slot(VisualElement page, string name, VisualElement region)
        {
            foreach (var carrier in new List<VisualElement>(page.Children()))
            {
                if (carrier.name != name) { continue; }
                foreach (var child in new List<VisualElement>(carrier.Children())) { region.Add(child); }
            }
        }

        /// <summary>Code-built content and code-behind hooks; a page with a template may need none.</summary>
        protected virtual void BuildContent() { }

        internal void Opened()
        {
            Ui.SetText(_title, Title.ToUpperInvariant());
            if (Template != null)
            {
                CloneTemplate();
                ViewModel = CreateViewModel();
                if (ViewModel != null) { ViewModel.Refresh(); }
                Root.dataSource = ViewModel;
            }
            OnOpen();
        }

        internal void Closed()
        {
            OnClose();
            if (ViewModel != null)
            {
                Root.dataSource = null;
                ViewModel.Dispose();
                ViewModel = null;
            }
        }

        /// <summary>Called each time the overlay opens (refresh data here).</summary>
        public virtual void OnOpen() { }

        /// <summary>Called when it closes; unsubscribe from state here.</summary>
        public virtual void OnClose() { }

        protected void Close() { Host.Close(this); }

        /// <summary>A title that depends on what the page shows (the character's name).</summary>
        protected void SetTitle(string title) { Ui.SetText(_title, (title ?? string.Empty).ToUpperInvariant()); }

        /// <summary>A size variant of the modal for what the page shows (a stat block is narrower than a sheet).</summary>
        protected void SetModalClass(string className, bool on) { _modal.EnableInClassList(className, on); }
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
            overlay.Opened();
            var root = overlay.Root;
            root.schedule.Execute(() => { root.RemoveFromClassList("cv-overlay--hidden"); }).StartingIn(16);
            return overlay;
        }

        public bool IsOpen(Overlay overlay) { return _stack.Contains(overlay); }

        public void Close(Overlay overlay)
        {
            if (!_stack.Remove(overlay)) { return; }
            overlay.Closed();
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
