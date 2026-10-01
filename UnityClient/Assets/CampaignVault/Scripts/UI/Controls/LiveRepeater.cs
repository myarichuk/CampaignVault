using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Unity.Properties;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Controls
{
    /// <summary>
    /// A list bound to an <c>ObservableCollection</c>: when the view model adds, removes or replaces one item, exactly
    /// one element is inserted, removed or swapped; every other element stays as it is (keyed DOM patching, not
    /// re-rendering the list). For lists that grow at the tail and trim at the head while the reader is looking at
    /// them (the story log, the toasts), where <see cref="Repeater"/>'s by-position reuse would re-bind everything.
    /// </summary>
    /// <remarks>The collection is set once; the binding never fires again because the collection instance never changes.</remarks>
    [UxmlElement]
    public partial class LiveRepeater : VisualElement
    {
        private IList _items;

        /// <summary>The item template under Resources/VaultUI/Templates; an <see cref="ITemplated"/> item may pick its own.</summary>
        [UxmlAttribute]
        public string itemTemplate { get; set; }

        [CreateProperty]
        public IList itemsSource
        {
            get { return _items; }
            set
            {
                if (ReferenceEquals(_items, value)) { return; }
                var old = _items as INotifyCollectionChanged;
                if (old != null) { old.CollectionChanged -= OnChanged; }
                _items = value;
                var live = value as INotifyCollectionChanged;
                if (live != null) { live.CollectionChanged += OnChanged; }
                RebuildAll();
            }
        }

        private void RebuildAll()
        {
            Clear();
            if (_items == null) { return; }
            for (int i = 0; i < _items.Count; i++) { Insert(i, Make(_items[i])); }
        }

        private VisualElement Make(object item)
        {
            var templated = item as ITemplated;
            var element = Templates.Clone(templated != null ? templated.Template : itemTemplate);
            element.dataSource = item;
            return element;
        }

        private void OnChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    for (int i = 0; i < e.NewItems.Count; i++) { Insert(e.NewStartingIndex + i, Make(e.NewItems[i])); }
                    break;
                case NotifyCollectionChangedAction.Remove:
                    for (int i = 0; i < e.OldItems.Count; i++)
                    {
                        if (e.OldStartingIndex < childCount) { RemoveAt(e.OldStartingIndex); }
                    }
                    break;
                case NotifyCollectionChangedAction.Replace:
                    for (int i = 0; i < e.NewItems.Count; i++)
                    {
                        int at = e.NewStartingIndex + i;
                        if (at < childCount) { RemoveAt(at); }
                        Insert(at, Make(e.NewItems[i]));
                    }
                    break;
                default:
                    RebuildAll();
                    break;
            }
        }
    }
}
