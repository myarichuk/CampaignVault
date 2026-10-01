using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// UnityClient/UI_CONVENTIONS.md, enforced: no inline style.* in Scripts/UI (looks belong in Theme/*.uss), and
    /// every UXML template loads. Files not moved to UXML yet are on a ratchet: their count may only go down, and the
    /// list empties by the end of phase 2.5 (U.6).
    /// </summary>
    public class UiConventionTests
    {
        /// <summary>Runtime values with no class equivalent: the one kind of inline style allowed.</summary>
        private static readonly string[] Allowed =
        {
            "_fill.style.width",                                 // VaultBar: the fill fraction
            "_ghost.style.width",                                // and its lagging ghost
            "_bubble.style.left",                                // tooltip at the pointer
            "_bubble.style.top",
            "style.rotate",                                      // VaultSpinner: the turning die
        };

        /// <summary>Not converted yet (U.3–U.6). Lower a number when a file loses inline styles; never raise one.</summary>
        private static readonly Dictionary<string, int> Legacy = new Dictionary<string, int>
        {
        };

        private static string UiScripts { get { return Path.Combine(Application.dataPath, "CampaignVault", "Scripts", "UI"); } }

        [Test]
        public void NoInlineStyles_OutsideTheAllowlist()
        {
            var style = new Regex(@"\.style\.");
            var problems = new List<string>();
            foreach (string path in Directory.GetFiles(UiScripts, "*.cs", SearchOption.AllDirectories))
            {
                string file = Path.GetFileName(path);
                var lines = File.ReadAllLines(path)
                    .Select((text, i) => new { text, line = i + 1 })
                    .Where(l => style.IsMatch(l.text) && !l.text.TrimStart().StartsWith("//") && !Allowed.Any(a => l.text.Contains(a)))
                    .ToList();
                int legacy;
                if (Legacy.TryGetValue(file, out legacy))
                {
                    if (lines.Count > legacy) { problems.Add(file + ": " + lines.Count + " inline styles, was " + legacy + " (move the new ones to USS)"); }
                    continue;
                }
                foreach (var l in lines) { problems.Add(file + ":" + l.line + ": " + l.text.Trim()); }
            }
            Assert.IsEmpty(problems, "Inline styles (use a USS class, or a ClassBinding for state):\n" + string.Join("\n", problems.ToArray()));
        }

        [Test]
        public void TheRatchet_OnlyListsFilesThatStillNeedIt()
        {
            var style = new Regex(@"\.style\.");
            foreach (var kv in Legacy)
            {
                string path = Directory.GetFiles(UiScripts, kv.Key, SearchOption.AllDirectories).FirstOrDefault();
                Assert.IsNotNull(path, kv.Key + " is gone: drop it from the ratchet");
                int count = File.ReadAllLines(path).Count(l => style.IsMatch(l) && !Allowed.Any(a => l.Contains(a)));
                Assert.AreEqual(kv.Value, count, kv.Key + " has " + count + " inline styles now: lower its ratchet to match");
            }
        }

        [Test]
        public void EveryTemplate_Loads()
        {
            var root = Path.Combine(Application.dataPath, "CampaignVault", "UI", "Resources", Templates.ResourceRoot);
            var files = Directory.GetFiles(root, "*.uxml", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files);
            foreach (string file in files)
            {
                string relative = file.Substring(root.Length).Replace('\\', '/');
                string path = relative.Substring(0, relative.Length - ".uxml".Length);
                var asset = Templates.Load(path);
                Assert.IsFalse(asset.importedWithErrors, path + " imported with errors");
                Assert.IsNotNull(asset.Instantiate(), path);
            }
        }
    }
}
