using System;
using System.Collections.Generic;
using Unity.Properties;

namespace CampaignVault.UnityClient.UI.Mvvm
{
    /// <summary>
    /// One row of an editable list (a companion's skill and its bonus, an attack's name, to-hit and damage): its cells side
    /// by side, then a button to take the row off. A header row is the same cells as captions, so the columns line up.
    /// Shown with Templates/Common/EntryRow.uxml.
    /// </summary>
    public sealed class EntryRowViewModel : ViewModel, IKeyed
    {
        private List<EntryCellViewModel> _cells;

        public EntryRowViewModel(string key, List<EntryCellViewModel> cells, string removeName, string removeHint, Action remove)
        {
            Key = key;
            _cells = cells;
            RemoveName = removeName ?? string.Empty;
            RemoveHint = removeHint ?? string.Empty;
            Remove = remove;
        }

        /// <summary>Captions over the columns: no remove button (its place is kept, so the columns still line up).</summary>
        public static EntryRowViewModel Header(List<EntryCellViewModel> captions)
        {
            return new EntryRowViewModel("header", captions, string.Empty, string.Empty, null) { IsHeader = true };
        }

        public string Key { get; private set; }
        [CreateProperty] public List<EntryCellViewModel> Cells { get { return _cells; } private set { SetList(ref _cells, value); } }
        [CreateProperty] public string RemoveName { get; private set; }
        [CreateProperty] public string RemoveHint { get; private set; }
        [CreateProperty] public Action Remove { get; private set; }
        [CreateProperty] public bool IsHeader { get; private set; }
        /// <summary>A cell takes the rest of the row (an attack's notes), so the cells stretch and the button sits at the end.</summary>
        [CreateProperty] public bool Stretches { get { return _cells.Exists(delegate (EntryCellViewModel c) { return c.IsWide; }); } }

        /// <summary>The cell for <paramref name="key"/>, or null.</summary>
        public EntryCellViewModel Cell(string key)
        {
            return _cells.Find(delegate (EntryCellViewModel c) { return c.Key == key; });
        }
    }

    /// <summary>How wide a cell is: a name, a short number, an ordinary value, or the rest of the row.</summary>
    public enum EntryWidth { Name, Narrow, Medium, Wide }

    /// <summary>
    /// One cell of an <see cref="EntryRowViewModel"/>: a caption (Common/EntryLabel) or a text box that commits on blur
    /// or Enter (Common/EntryInput).
    /// </summary>
    public sealed class EntryCellViewModel : ViewModel, IKeyed, ITemplated
    {
        private readonly Action<string> _changed;
        private string _value = string.Empty;
        private string _seen;

        private EntryCellViewModel(string key, EntryWidth width, Action<string> changed)
        {
            Key = key;
            Width = width;
            _changed = changed;
        }

        public static EntryCellViewModel Label(string key, string caption, EntryWidth width)
        {
            return new EntryCellViewModel(key, width, null) { Caption = caption ?? string.Empty, Name = string.Empty, Hint = string.Empty };
        }

        public static EntryCellViewModel Input(string key, string name, string hint, EntryWidth width, Action<string> changed)
        {
            return new EntryCellViewModel(key, width, changed) { Caption = string.Empty, Name = name ?? string.Empty, Hint = hint ?? string.Empty };
        }

        public string Key { get; private set; }
        public EntryWidth Width { get; private set; }
        public bool IsInput { get { return _changed != null; } }
        public string Template { get { return IsInput ? "Common/EntryInput" : "Common/EntryLabel"; } }

        [CreateProperty] public string Caption { get; private set; }
        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Hint { get; private set; }
        [CreateProperty] public bool IsName { get { return Width == EntryWidth.Name; } }
        [CreateProperty] public bool IsNarrow { get { return Width == EntryWidth.Narrow; } }
        [CreateProperty] public bool IsWide { get { return Width == EntryWidth.Wide; } }

        [CreateProperty]
        public string Value
        {
            get { return _value; }
            set { if (Set(ref _value, value ?? string.Empty)) { _seen = _value; if (_changed != null) { _changed(_value); } } }
        }

        /// <summary>From state: shown (only when it changed there), not sent back.</summary>
        public void Show(string value)
        {
            value = value ?? string.Empty;
            if (value == _seen) { return; }
            _seen = value;
            Set(ref _value, value, "Value");
        }
    }
}
