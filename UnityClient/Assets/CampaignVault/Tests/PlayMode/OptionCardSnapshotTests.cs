using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Builder;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// The builder's option card with everything on it at once: a long title, the HOMEBREW chip and the DM'S PICK chip.
    /// A DM-written background ("Black Ledger Courier", homebrew and suggested) once squeezed its title to a sliver and
    /// pushed the chips past the card's edge. Photographed, and measured: every chip inside its card, the title readable.
    /// </summary>
    public class OptionCardSnapshotTests
    {
        [UnityTest]
        public IEnumerator ALongTitleWithBothChips_StaysInsideTheCard()
        {
            var cards = new List<VisualElement>();
            yield return UiSnapshotTests.Capture(Templates.Load("Builder/OptionCard"), "option-card-chips", delegate (VisualElement root)
            {
                root.Clear();
                var grid = new VisualElement();
                grid.AddToClassList("cv-options");
                grid.style.width = 640;
                grid.style.paddingLeft = 24;
                grid.style.paddingTop = 24;
                root.Add(grid);
                foreach (var option in new[]
                {
                    Card("Black Ledger Courier", "You carried sealed letters, coin and ledgers for a network that never signed its name.", true, true),
                    Card("Wood Road Outrider", "You rode escort on caravans along the forest road, guarding goods for merchants who paid well.", true, true),
                    Card("Acolyte", "You have spent your life in service to a temple.", false, false),
                    Card("Ward Debt Collector", "You grew up as the family enforcer in a dockside ward.", true, false),
                })
                {
                    var card = Templates.Clone("Builder/OptionCard");
                    card.dataSource = option;
                    grid.Add(card);
                    cards.Add(card);
                }
            });

            foreach (var card in cards)
            {
                var head = card.Q(className: "cv-option__head");
                var label = card.Q<Label>(className: "cv-option__label");
                Assert.IsTrue(card.worldBound.width > 0, "the card laid out");
                Assert.GreaterOrEqual(label.worldBound.width, 120f, label.text + ": the title keeps a readable width");
                foreach (var chip in head.Query(className: "cv-chip").ToList())
                {
                    if (chip.ClassListContains("cv-hidden")) { continue; }
                    Assert.LessOrEqual(chip.worldBound.xMax, card.worldBound.xMax + 0.5f, label.text + ": a chip hangs past the card's right edge");
                    Assert.GreaterOrEqual(chip.worldBound.xMin, card.worldBound.xMin - 0.5f, label.text + ": a chip hangs past the card's left edge");
                    var chipLabel = chip.Q<Label>();
                    Assert.LessOrEqual(chipLabel.worldBound.xMax, chip.worldBound.xMax + 0.5f, label.text + ": a chip's text is clipped");
                }
            }
        }

        private static OptionViewModel Card(string label, string description, bool homebrew, bool suggested)
        {
            var option = new BuilderOption { Id = label.ToLowerInvariant().Replace(' ', '_'), Label = label, Description = description, Homebrew = homebrew };
            var vm = new OptionViewModel("opt-" + option.Id, option, delegate { });
            vm.Update(option, false, suggested, false, string.Empty);
            return vm;
        }
    }
}
