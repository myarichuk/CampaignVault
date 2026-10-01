using System;
using System.Collections;
using Unity.Properties;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Controls
{
    /// <summary>
    /// A list bound to a view model's list property: one cloned template per item, each item the data source of its
    /// element (an ItemsControl, in WPF terms). Unlike ListView it doesn't virtualize or scroll, so it sits inside the
    /// page's own scroll view and lays out with flex like any other content. Elements are reused by position, so a
    /// refresh that keeps the items keeps focus and hover.
    /// </summary>
    [UxmlElement]
    public partial class Repeater : VisualElement
    {
        private IList _items;

        /// <summary>The item template under Resources/VaultUI/Templates; an <see cref="ITemplated"/> item may pick its own.</summary>
        [UxmlAttribute]
        public string itemTemplate { get; set; }

        [CreateProperty]
        public IList itemsSource
        {
            get { return _items; }
            set { _items = value; Rebuild(); }
        }

        private void Rebuild()
        {
            int count = _items == null ? 0 : _items.Count;
            for (int i = 0; i < count; i++)
            {
                object item = _items[i];
                var templated = item as ITemplated;
                string template = templated != null ? templated.Template : itemTemplate;
                var child = i < childCount ? this[i] : null;
                if (child == null || !Equals(child.userData, template))
                {
                    var made = Templates.Clone(template);
                    made.userData = template;
                    if (child != null) { RemoveAt(i); }
                    Insert(i, made);
                    child = made;
                }
                child.dataSource = item;
            }
            while (childCount > count) { RemoveAt(childCount - 1); }
        }
    }

    /// <summary>
    /// Shows one view model with the template it names (a ContentPresenter with a DataTemplate per type, in WPF
    /// terms): the builder's current step is a pick list, an ability table or a spell book depending on its kind.
    /// </summary>
    [UxmlElement]
    public partial class ContentPresenter : VisualElement
    {
        private object _content;
        private string _template;

        [CreateProperty]
        public object content
        {
            get { return _content; }
            set
            {
                _content = value;
                var templated = value as ITemplated;
                string template = templated != null ? templated.Template : null;
                if (template != _template)
                {
                    Clear();
                    _template = template;
                    if (template != null) { Add(Templates.Clone(template)); }
                }
                // The child, not this element: this element's own binding reads from its parent's source.
                if (childCount > 0) { this[0].dataSource = value; }
            }
        }
    }

    /// <summary>
    /// Toggles a USS class from a bound value: true, a non-empty string or a non-zero number adds it. Visual state
    /// (selected, current, error, hidden) stays a class the theme styles, never an inline style.
    /// <code>&lt;cv:ClassBinding property="cls-selected" data-source-path="Selected" class-name="cv-option--selected" /&gt;</code>
    /// </summary>
    /// <remarks><c>property</c> only names the binding (one per element); this binding writes no property.</remarks>
    [UxmlObject]
    public partial class ClassBinding : DataBinding
    {
        /// <summary>One class, or several separated by spaces.</summary>
        [UxmlAttribute]
        public string className { get; set; }

        /// <summary>Add the class when the value is false/empty instead (cv-hidden on "has nothing to show").</summary>
        [UxmlAttribute]
        public bool invert { get; set; }

        public ClassBinding()
        {
            bindingMode = BindingMode.ToTarget;
        }

        public ClassBinding(string path, string className, bool invert = false) : this()
        {
            dataSourcePath = new PropertyPath(path);
            this.className = className;
            this.invert = invert;
        }

        protected override BindingResult UpdateUI<TValue>(in BindingContext context, ref TValue value)
        {
            if (string.IsNullOrEmpty(className)) { return new BindingResult(BindingStatus.Failure, "ClassBinding without class-name"); }
            bool on = Truthy(value);
            foreach (string c in className.Split(' '))
            {
                if (c.Length > 0) { context.targetElement.EnableInClassList(c, invert ? !on : on); }
            }
            return new BindingResult(BindingStatus.Success);
        }

        protected override BindingResult UpdateSource<TValue>(in BindingContext context, ref TValue value)
        {
            return new BindingResult(BindingStatus.Success);
        }

        public static bool Truthy(object value)
        {
            if (value == null) { return false; }
            if (value is bool) { return (bool)value; }
            var s = value as string;
            if (s != null) { return s.Length > 0; }
            if (value is int) { return (int)value != 0; }
            if (value is float) { return (float)value != 0f; }
            if (value is double) { return (double)value != 0d; }
            var list = value as ICollection;
            if (list != null) { return list.Count > 0; }
            return true;
        }
    }

    /// <summary>
    /// The cv-btn: optional icon, then a small-caps label, and a bindable <see cref="command"/> run on click (or on
    /// Enter/gamepad submit). Enablement binds to <c>enabledSelf</c>; a hover hint to <c>tooltip</c>.
    /// </summary>
    [UxmlElement]
    public partial class VaultButton : Button
    {
        private VisualElement _icon;
        private Label _label;
        private string _iconName = string.Empty;

        public VaultButton()
        {
            AddToClassList("cv-btn");
            clicked += OnClicked;
        }

        public VaultButton(string label, string icon, Action command) : this()
        {
            this.label = label;
            this.icon = icon;
            this.command = command;
        }

        [UxmlAttribute, CreateProperty]
        public string label
        {
            get { return _label != null ? _label.text : string.Empty; }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    if (_label != null) { _label.RemoveFromHierarchy(); _label = null; }
                    return;
                }
                if (_label == null)
                {
                    _label = new Label();
                    _label.enableRichText = false;
                    _label.pickingMode = PickingMode.Ignore;
                    _label.AddToClassList("cv-btn__label");
                    Add(_label);
                }
                _label.text = value;
            }
        }

        /// <summary>An icon name from Theme/icons.uss (cv-icon--&lt;name&gt;), shown before the label.</summary>
        [UxmlAttribute, CreateProperty]
        public string icon
        {
            get { return _iconName; }
            set
            {
                value = value ?? string.Empty;
                if (value == _iconName) { return; }
                if (_icon != null) { _icon.RemoveFromHierarchy(); _icon = null; }
                _iconName = value;
                if (value.Length == 0) { return; }
                _icon = new VisualElement();
                _icon.pickingMode = PickingMode.Ignore;
                _icon.AddToClassList("cv-icon");
                _icon.AddToClassList("cv-icon--" + value);
                Insert(0, _icon);
            }
        }

        [CreateProperty]
        public Action command { get; set; }

        /// <summary>Without the cv-btn look (a codex tab is styled by its own class).</summary>
        [UxmlAttribute]
        public bool plain
        {
            get { return !ClassListContains("cv-btn"); }
            set { EnableInClassList("cv-btn", !value); }
        }

        /// <summary>No click sound (a button that plays its own cue).</summary>
        [UxmlAttribute]
        public bool silent { get; set; }

        private void OnClicked()
        {
            if (!silent) { VaultSfx.Play(VaultSfx.Cue.Click); }
            if (command != null) { command(); }
        }
    }

    /// <summary>
    /// A card that acts on click (a party frame opens the sheet) without looking like a button. A button inside it
    /// (untrack) keeps its own click.
    /// </summary>
    [UxmlElement]
    public partial class ClickArea : VisualElement
    {
        public ClickArea()
        {
            RegisterCallback<ClickEvent>(OnClick);
        }

        [CreateProperty]
        public Action command { get; set; }

        private void OnClick(ClickEvent e)
        {
            for (var t = e.target as VisualElement; t != null && t != this; t = t.parent)
            {
                if (t is Button) { return; }
            }
            if (command == null) { return; }
            VaultSfx.Play(VaultSfx.Cue.Click);
            command();
        }
    }

    /// <summary>The cv-field text input with a bindable placeholder that hides on focus.</summary>
    [UxmlElement]
    public partial class VaultField : TextField
    {
        public VaultField()
        {
            AddToClassList("cv-field");
            textEdition.hidePlaceholderOnFocus = true;
        }

        [UxmlAttribute, CreateProperty]
        public string placeholder
        {
            get { return textEdition.placeholder; }
            set { textEdition.placeholder = value ?? string.Empty; }
        }
    }
}
