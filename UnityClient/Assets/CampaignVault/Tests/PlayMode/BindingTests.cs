using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Properties;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Controls;
using CampaignVault.UnityClient.UI.Mvvm;
using Object = UnityEngine.Object;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// The binding plumbing every page relies on, in a real runtime panel (Templates/Dev/BindingSpike.uxml): two-way
    /// text, bound placeholders, class toggles, commands and enablement, bound element names, a nested model path,
    /// repeated templates (view models and plain strings) and a presenter that picks its template by view model.
    /// </summary>
    public class BindingTests
    {
        private sealed class Item : ViewModel, IKeyed
        {
            private string _label;
            private bool _selected;

            public Item(string key, Action<Item> pick)
            {
                Key = key;
                Pick = delegate { pick(this); };
            }

            public string Key { get; private set; }

            [CreateProperty] public string Name { get { return "item-" + Key; } }
            [CreateProperty] public string Label { get { return _label; } set { Set(ref _label, value); } }
            [CreateProperty] public bool Selected { get { return _selected; } set { Set(ref _selected, value); } }
            [CreateProperty] public Action Pick { get; private set; }
        }

        private sealed class Content : ViewModel, ITemplated
        {
            public string Template { get { return "Dev/SpikeContent"; } }
            [CreateProperty] public string Text { get; set; }
        }

        private sealed class Spike : ViewModel
        {
            private string _name = string.Empty;
            private string _placeholder = string.Empty;
            private bool _flag;
            private bool _canPress = true;
            private CharacterSheet _sheet;
            private List<Item> _items = new List<Item>();
            private List<string> _paragraphs = new List<string>();
            private object _content;

            public int Presses;

            public Spike() { Press = delegate { Presses++; }; }

            [CreateProperty] public string Name { get { return _name; } set { Set(ref _name, value ?? string.Empty); } }
            [CreateProperty] public string Placeholder { get { return _placeholder; } set { Set(ref _placeholder, value); } }
            [CreateProperty] public bool Flag { get { return _flag; } set { Set(ref _flag, value); } }
            [CreateProperty] public bool CanPress { get { return _canPress; } set { Set(ref _canPress, value); } }
            [CreateProperty] public Action Press { get; private set; }
            [CreateProperty] public CharacterSheet Sheet { get { return _sheet; } set { Set(ref _sheet, value); } }
            [CreateProperty] public List<Item> Items { get { return _items; } set { SetList(ref _items, value); } }
            [CreateProperty] public List<string> Paragraphs { get { return _paragraphs; } set { SetList(ref _paragraphs, value); } }
            [CreateProperty] public object Content { get { return _content; } set { Set(ref _content, value); } }
        }

        private GameObject _go;
        private RenderTexture _rt;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) { Object.Destroy(_go); }
            if (_rt != null) { _rt.Release(); }
        }

        private static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) { yield return null; } }

        private static void Submit(Button button)
        {
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
        }

        [UnityTest]
        public IEnumerator EveryBindingKind_UpdatesThePanel()
        {
            var settings = Object.Instantiate(Resources.Load<PanelSettings>("VaultUI/VaultPanelSettings"));
            _rt = new RenderTexture(800, 600, 24, RenderTextureFormat.ARGB32);
            settings.targetTexture = _rt;
            _go = new GameObject("BindingSpike");
            var doc = _go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = Templates.Load("Dev/BindingSpike");
            yield return null;
            var root = doc.rootVisualElement;
            Assert.IsNotNull(root.Q("spike-root"), "the template cloned");

            var vm = new Spike { Name = "Aric", Placeholder = "what the table calls them", Sheet = new CharacterSheet { Name = "Wren" } };
            var picked = new List<string>();
            Action<Item> pick = delegate (Item i) { picked.Add(i.Key); };
            vm.Items = new List<Item> { new Item("a", pick) { Label = "Alpha" }, new Item("b", pick) { Label = "Beta", Selected = true } };
            vm.Paragraphs = new List<string> { "one", "two" };
            vm.Content = new Content { Text = "presented" };
            root.dataSource = vm;
            yield return Frames(3);

            // To the view: text, a nested plain field, a custom control's property, a static placeholder attribute.
            Assert.AreEqual("Aric", root.Q<Label>("spike-name-echo").text);
            var field = root.Q<VaultField>("spike-name");
            Assert.AreEqual("Aric", field.value);
            Assert.AreEqual("what the table calls them", field.placeholder);
            Assert.AreEqual("static hint", root.Q<TextField>("spike-static-placeholder").textEdition.placeholder);
            Assert.AreEqual("Wren", root.Q<Label>("spike-sheet-name").text, "a path into a plain model (public fields)");

            // From the view: typing reaches the view model.
            field.value = "Aric Thorne";
            yield return Frames(2);
            Assert.AreEqual("Aric Thorne", vm.Name, "two-way");
            Assert.AreEqual("Aric Thorne", root.Q<Label>("spike-name-echo").text);

            // Class toggles, both ways round.
            var flag = root.Q("spike-flag");
            Assert.IsFalse(flag.ClassListContains("spike--on"));
            Assert.IsTrue(flag.ClassListContains("cv-hidden"));
            vm.Flag = true;
            yield return Frames(2);
            Assert.IsTrue(flag.ClassListContains("spike--on"));
            Assert.IsFalse(flag.ClassListContains("cv-hidden"));

            // Commands and enablement.
            var press = root.Q<VaultButton>("spike-press");
            Assert.AreEqual("PRESS", press.label);
            Assert.AreEqual("a hint", press.tooltip);
            Submit(press);
            Assert.AreEqual(1, vm.Presses);
            vm.CanPress = false;
            yield return Frames(2);
            Assert.IsFalse(press.enabledSelf);

            // A repeated template: names, labels, classes and commands per item.
            var items = root.Q<Repeater>("spike-items");
            Assert.AreEqual(2, items.childCount);
            var alpha = root.Q<VaultButton>("item-a");
            Assert.IsNotNull(alpha, "the element name is bound from the item");
            Assert.AreEqual("Alpha", alpha.label);
            Assert.IsTrue(root.Q<VaultButton>("item-b").ClassListContains("cv-option--selected"));
            Submit(alpha);
            CollectionAssert.AreEqual(new[] { "a" }, picked);

            // An item's own change touches only its element; a new list reuses elements by position.
            vm.Items[0].Label = "Alpha Prime";
            yield return Frames(2);
            Assert.AreEqual("Alpha Prime", alpha.label);
            vm.Items = new List<Item> { vm.Items[0], vm.Items[1], new Item("c", pick) { Label = "Gamma" } };
            yield return Frames(2);
            Assert.AreEqual(3, items.childCount);
            Assert.AreSame(alpha, items[0], "kept, not rebuilt");
            Assert.AreEqual("Gamma", root.Q<VaultButton>("item-c").label);
            vm.Items = new List<Item> { vm.Items[2] };
            yield return Frames(2);
            Assert.AreEqual(1, items.childCount);
            Assert.AreEqual("item-c", items[0].name);

            // Plain strings as items (the binding reads the item itself).
            var paragraphs = root.Q<Repeater>("spike-paragraphs");
            Assert.AreEqual(2, paragraphs.childCount);
            Assert.AreEqual("two", ((Label)paragraphs[1]).text);

            // A presenter picks the template the view model names.
            Assert.AreEqual("presented", root.Q<Label>("spike-content-text").text);
            vm.Content = new Content { Text = "swapped" };
            yield return Frames(2);
            Assert.AreEqual("swapped", root.Q<Label>("spike-content-text").text);
        }
    }
}
