using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The story: layout in Templates/Story/StoryLog.uxml, one template per kind of segment, state in
    /// <see cref="StoryLogViewModel"/>. What is left here is the scrolling a binding can't do: the view follows new
    /// text only while the reader is at the bottom (the view model keeps the pin and the LATEST button).
    /// </summary>
    public sealed class StoryLogView
    {
        private const float PinSlack = 48f;

        private readonly StoryLogViewModel _vm;
        private readonly ScrollView _scroll;
        private bool _programmatic;

        public StoryLogView(VisualElement host, VaultAppState state, Later later)
        {
            _vm = new StoryLogViewModel(state, later);
            Templates.CloneInto("Story/StoryLog", host);
            host.dataSource = _vm;
            _scroll = host.Q<ScrollView>("StoryScroll");
            // Elastic overscroll fights programmatic follow (the view drifts off the last line).
            _scroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped;
            // Only the reader moves the pin; content growth never does.
            _scroll.verticalScroller.valueChanged += delegate (float v)
            {
                if (_programmatic) { return; }
                _vm.SetPinned(v >= _scroll.verticalScroller.highValue - PinSlack);
            };
            _scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(delegate { if (_vm.Pinned) { ScrollToEnd(); } });
            _vm.propertyChanged += delegate (object sender, BindablePropertyChangedEventArgs e)
            {
                if (e.propertyName == "ScrollTick") { ScrollToEnd(); }
            };
        }

        public StoryLogViewModel ViewModel { get { return _vm; } }
        /// <summary>Items currently in the log, one per transcript segment (tests, diagnostics).</summary>
        public int EntryCount { get { return _vm.EntryCount; } }
        public ScrollView ScrollView { get { return _scroll; } }

        /// <summary>Snap to the newest line and follow from here on.</summary>
        public void Pin() { _vm.Pin(); }

        private void ScrollToEnd()
        {
            float content = _scroll.contentContainer.layout.height;
            float viewport = _scroll.contentViewport.layout.height;
            if (float.IsNaN(content) || float.IsNaN(viewport)) { return; }
            _programmatic = true;
            _scroll.scrollOffset = new Vector2(0f, Mathf.Max(0f, content - viewport));
            _programmatic = false;
        }
    }
}
