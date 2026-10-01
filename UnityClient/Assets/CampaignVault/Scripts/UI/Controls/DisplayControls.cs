using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;

namespace CampaignVault.UnityClient.UI.Controls
{
    /// <summary>
    /// A bar (hit points, a need) with a lagging "damage ghost" behind the fill. <see cref="fraction"/> is bindable;
    /// mid and low are classes the theme colors.
    /// </summary>
    [UxmlElement]
    public partial class VaultBar : VisualElement
    {
        private readonly VisualElement _ghost;
        private readonly VisualElement _fill;
        private float _fraction = 1f;
        private bool _hasValue;

        public VaultBar()
        {
            AddToClassList("cv-bar");
            _ghost = new VisualElement();
            _ghost.AddToClassList("cv-bar__ghost");
            Add(_ghost);
            _fill = new VisualElement();
            _fill.AddToClassList("cv-bar__fill");
            Add(_fill);
            Apply();
        }

        [UxmlAttribute, CreateProperty]
        public float fraction
        {
            get { return _fraction; }
            set
            {
                _fraction = Mathf.Clamp01(value);
                if (!_hasValue)
                {
                    // The first value (from a binding, a frame after the bar is on screen) is where it starts, not a
                    // hit: no damage ghost sliding down from full when a sheet opens.
                    _hasValue = true;
                    AddToClassList("cv-bar--settling");
                    schedule.Execute(() => RemoveFromClassList("cv-bar--settling")).StartingIn(100);
                }
                Apply();
            }
        }

        private void Apply()
        {
            // A fill fraction is a runtime value with no class equivalent: the allowed kind of inline style.
            _fill.style.width = Length.Percent(_fraction * 100f);
            _ghost.style.width = Length.Percent(_fraction * 100f);
            EnableInClassList("cv-bar--mid", _fraction <= 0.6f && _fraction > 0.3f);
            EnableInClassList("cv-bar--low", _fraction <= 0.3f);
        }
    }

    /// <summary>
    /// An icon that turns while <see cref="spinning"/> (the thinking die). The rotation is a runtime value with no
    /// class equivalent, the allowed kind of inline style; the timer runs only while it spins.
    /// </summary>
    [UxmlElement]
    public partial class VaultSpinner : VisualElement
    {
        private IVisualElementScheduledItem _tick;
        private bool _spinning;
        private float _angle;

        public VaultSpinner()
        {
            pickingMode = PickingMode.Ignore;
            RegisterCallback<DetachFromPanelEvent>(delegate { Stop(); });
            RegisterCallback<AttachToPanelEvent>(delegate { if (_spinning) { Start(); } });
        }

        [UxmlAttribute, CreateProperty]
        public bool spinning
        {
            get { return _spinning; }
            set
            {
                if (_spinning == value) { return; }
                _spinning = value;
                if (value) { Start(); } else { Stop(); }
            }
        }

        private void Start()
        {
            if (_tick != null || panel == null) { return; }
            _tick = schedule.Execute(() =>
            {
                _angle = (_angle + 6f) % 360f;
                style.rotate = new Rotate(_angle);
            }).Every(33);
        }

        private void Stop()
        {
            if (_tick == null) { return; }
            _tick.Pause();
            _tick = null;
        }
    }

    /// <summary>
    /// Sets one class from a bound value: <c>class-prefix</c> + value ("cv-chip--" + "gold", "cv-check--rank" + 3),
    /// replacing whichever class with that prefix was there. An empty value leaves none. For a variant the data
    /// picks; a yes/no state is a <see cref="ClassBinding"/>.
    /// <code>&lt;cv:VariantBinding property="var-tone" data-source-path="Tone" class-prefix="cv-chip--" /&gt;</code>
    /// </summary>
    [UxmlObject]
    public partial class VariantBinding : DataBinding
    {
        [UxmlAttribute]
        public string classPrefix { get; set; }

        public VariantBinding()
        {
            bindingMode = BindingMode.ToTarget;
        }

        protected override BindingResult UpdateUI<TValue>(in BindingContext context, ref TValue value)
        {
            if (string.IsNullOrEmpty(classPrefix)) { return new BindingResult(BindingStatus.Failure, "VariantBinding without class-prefix"); }
            var target = context.targetElement;
            var stale = new System.Collections.Generic.List<string>();
            foreach (string c in target.GetClasses()) { if (c.StartsWith(classPrefix, System.StringComparison.Ordinal)) { stale.Add(c); } }
            foreach (string c in stale) { target.RemoveFromClassList(c); }
            string variant = value == null ? string.Empty : value.ToString();
            if (variant.Length > 0) { target.AddToClassList(classPrefix + variant); }
            return new BindingResult(BindingStatus.Success);
        }

        protected override BindingResult UpdateSource<TValue>(in BindingContext context, ref TValue value)
        {
            return new BindingResult(BindingStatus.Success);
        }
    }
}
