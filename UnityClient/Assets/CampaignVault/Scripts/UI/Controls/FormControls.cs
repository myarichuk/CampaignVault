using Unity.Properties;
using UnityEngine.UIElements;

namespace CampaignVault.UnityClient.UI.Controls
{
    /// <summary>
    /// An on/off switch with a caption (a checkbox, in web terms). <see cref="value"/> binds two-way: a click flips it
    /// and tells the view model; a view model's change repaints it. Nothing fires for a value that didn't change.
    /// </summary>
    [UxmlElement]
    public partial class VaultSwitch : VisualElement
    {
        private static readonly BindingId ValueProperty = nameof(value);
        private readonly Label _caption;
        private bool _on;

        public VaultSwitch()
        {
            AddToClassList("cv-switch");
            var track = new VisualElement();
            track.AddToClassList("cv-switch__track");
            track.pickingMode = PickingMode.Ignore;
            var knob = new VisualElement();
            knob.AddToClassList("cv-switch__knob");
            knob.pickingMode = PickingMode.Ignore;
            track.Add(knob);
            Add(track);
            _caption = new Label();
            _caption.AddToClassList("cv-body");
            _caption.enableRichText = false;
            _caption.pickingMode = PickingMode.Ignore;
            Add(_caption);
            RegisterCallback<ClickEvent>(OnClick);
        }

        [UxmlAttribute, CreateProperty]
        public string text
        {
            get { return _caption.text; }
            set { _caption.text = value ?? string.Empty; }
        }

        [UxmlAttribute, CreateProperty]
        public bool value
        {
            get { return _on; }
            set
            {
                if (_on == value) { return; }
                _on = value;
                EnableInClassList("cv-switch--on", value);
                NotifyPropertyChanged(in ValueProperty);
            }
        }

        private void OnClick(ClickEvent e)
        {
            if (!enabledInHierarchy) { return; }
            VaultSfx.Play(VaultSfx.Cue.Click);
            value = !_on;
        }
    }

    /// <summary>The cv-card: a framed group with an optional small-caps title as its first line.</summary>
    [UxmlElement]
    public partial class VaultCard : VisualElement
    {
        private Label _title;

        public VaultCard()
        {
            AddToClassList("cv-card");
        }

        [UxmlAttribute, CreateProperty]
        public string title
        {
            get { return _title != null ? _title.text : string.Empty; }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    if (_title != null) { _title.RemoveFromHierarchy(); _title = null; }
                    return;
                }
                if (_title == null)
                {
                    _title = new Label();
                    _title.enableRichText = false;
                    _title.AddToClassList("cv-caption");
                    _title.AddToClassList("cv-card__title");
                    Insert(0, _title);
                }
                _title.text = value;
            }
        }
    }
}
