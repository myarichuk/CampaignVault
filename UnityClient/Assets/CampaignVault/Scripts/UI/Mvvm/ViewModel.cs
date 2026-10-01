using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.UI.Mvvm
{
    /// <summary>
    /// Base for anything a UXML layout binds to (the data context, in WPF terms). Properties raise a change only when
    /// their value really changed, so the binding system touches only the elements that show it: typing, focus and
    /// hover survive every state update.
    /// </summary>
    /// <remarks>
    /// Page view models <see cref="Watch"/> the state areas they show and rebuild their properties in
    /// <see cref="Refresh"/>; they never write state themselves, they call the controller. Item view models (a card,
    /// a row) are plain view models their page keeps in step with <see cref="ItemList"/>.
    /// </remarks>
    public abstract class ViewModel : INotifyBindablePropertyChanged, IDisposable
    {
        private VaultAppState _watched;
        private StateArea _areas;

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        /// <summary>Re-read state into the properties. Called on every change to a watched area.</summary>
        public virtual void Refresh() { }

        /// <summary>Refresh whenever one of these state areas changes, until disposed.</summary>
        protected void Watch(VaultAppState state, StateArea areas)
        {
            Unwatch();
            _watched = state;
            _areas = areas;
            state.Changed += OnStateChanged;
        }

        private void OnStateChanged(StateArea area)
        {
            if ((area & _areas) != 0) { Refresh(); }
        }

        private void Unwatch()
        {
            if (_watched != null) { _watched.Changed -= OnStateChanged; _watched = null; }
        }

        public virtual void Dispose() { Unwatch(); }

        protected void Notify([CallerMemberName] string property = null)
        {
            var handler = propertyChanged;
            if (handler != null) { handler(this, new BindablePropertyChangedEventArgs(property)); }
        }

        /// <summary>Assign and notify, only when the value changed.</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
            field = value;
            Notify(property);
            return true;
        }

        /// <summary>
        /// A list property: keeps the old instance (and notifies nothing) while the items are the same, in the same
        /// order. Bound lists are replaced, never edited in place, so a repeater sees every change.
        /// </summary>
        protected bool SetList<T>(ref List<T> field, List<T> value, [CallerMemberName] string property = null)
        {
            if (field != null && value != null && field.Count == value.Count)
            {
                bool same = true;
                var comparer = EqualityComparer<T>.Default;
                for (int i = 0; i < value.Count && same; i++) { same = comparer.Equals(field[i], value[i]); }
                if (same) { return false; }
            }
            field = value;
            Notify(property);
            return true;
        }
    }

    /// <summary>A view model chosen by type: a <see cref="Controls.ContentPresenter"/> or <see cref="Controls.Repeater"/> clones its template.</summary>
    public interface ITemplated
    {
        /// <summary>The template's path under Resources/VaultUI/Templates, without ".uxml".</summary>
        string Template { get; }
    }

    /// <summary>An item view model with a stable identity, so <see cref="ItemList"/> can keep it across refreshes.</summary>
    public interface IKeyed
    {
        string Key { get; }
    }

    /// <summary>Keeps a page's item view models in step with the state they show.</summary>
    public static class ItemList
    {
        /// <summary>
        /// Items for <paramref name="source"/>: an existing item with the same key is reused and updated (so its
        /// element keeps focus and hover), a new key is created. Returns the same list instance when the keys are
        /// unchanged, so the list property raises nothing; each item raises its own changes.
        /// </summary>
        public static List<TItem> Sync<TSource, TItem>(List<TItem> current, IEnumerable<TSource> source, Func<TSource, string> key,
            Func<TSource, TItem> create, Action<TItem, TSource> update) where TItem : IKeyed
        {
            var byKey = new Dictionary<string, TItem>(StringComparer.Ordinal);
            if (current != null) { foreach (var item in current) { byKey[item.Key] = item; } }
            var next = new List<TItem>();
            foreach (var s in source)
            {
                TItem item;
                if (!byKey.TryGetValue(key(s), out item)) { item = create(s); }
                update(item, s);
                next.Add(item);
            }
            if (current != null && current.Count == next.Count)
            {
                bool same = true;
                for (int i = 0; i < next.Count && same; i++) { same = ReferenceEquals(current[i], next[i]); }
                if (same) { return current; }
            }
            return next;
        }
    }
}
