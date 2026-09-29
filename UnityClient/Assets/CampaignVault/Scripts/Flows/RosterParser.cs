using System.Collections.Generic;

namespace CampaignVault.UnityClient.Flows
{
    /// <summary>
    /// Tolerant roster parsing: the onboarding wizard accepts a pasted party
    /// list in whatever shape the table already has ("Name — detail",
    /// "Name: detail", "• Name (detail)", bare names) and turns it into
    /// structured entries plus the party line the DM prompt needs.
    /// Pure C#, covered by the standalone probe.
    /// </summary>
    public sealed class RosterEntry
    {
        public string Name = string.Empty;
        public string Detail = string.Empty;
    }

    public static class RosterParser
    {
        public static List<RosterEntry> Parse(string text)
        {
            var entries = new List<RosterEntry>();
            if (string.IsNullOrEmpty(text)) { return entries; }
            string[] lines = text.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim().TrimStart('•', '*', '-', '>');
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) { continue; }
                entries.Add(SplitLine(line));
            }
            return entries;
        }

        private static RosterEntry SplitLine(string line)
        {
            int cut = IndexOfSeparator(line, "\u2014"); // em dash
            if (cut < 0) { cut = IndexOfSeparator(line, "\u2013"); } // en dash
            if (cut < 0) { cut = IndexOfSeparator(line, " - "); }
            if (cut >= 0)
            {
                return new RosterEntry
                {
                    Name = line.Substring(0, cut).Trim(),
                    Detail = line.Substring(cut + SeparatorLength(line, cut)).Trim(),
                };
            }
            int colon = line.IndexOf(':');
            if (colon > 0 && colon < 42 && !line.Substring(0, colon).Contains(" "))
            {
                // "Name: detail" only when the head is a single token (avoids
                // splitting prose like "Note: ...").
                return new RosterEntry
                {
                    Name = line.Substring(0, colon).Trim(),
                    Detail = line.Substring(colon + 1).Trim(),
                };
            }
            int paren = line.IndexOf('(');
            if (paren > 0 && line.EndsWith(")"))
            {
                return new RosterEntry
                {
                    Name = line.Substring(0, paren).Trim(),
                    Detail = line.Substring(paren + 1, line.Length - paren - 2).Trim(),
                };
            }
            return new RosterEntry { Name = line };
        }

        private static int IndexOfSeparator(string line, string sep)
        {
            int at = line.IndexOf(sep);
            if (at <= 0) { return -1; }
            if (at + sep.Length >= line.Length) { return -1; }
            return at;
        }

        private static int SeparatorLength(string line, int cut)
        {
            if (line.Substring(cut, 1) == " ") { return 3; }
            return line.Substring(cut, 1) == "-" ? 3 : 1;
        }

        /// <summary>One line per non-empty entry, for openThreads-style fields.</summary>
        public static List<string> ParseLines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { return lines; }
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim().TrimStart('•', '*', '-', '>');
                line = line.Trim();
                if (!string.IsNullOrEmpty(line)) { lines.Add(line); }
            }
            return lines;
        }

        /// <summary>"id | stance" (or em/en dash) per line for npcsInPlay.</summary>
        public static List<NpcStanceRow> ParseStances(string text)
        {
            var rows = new List<NpcStanceRow>();
            foreach (string line in ParseLines(text))
            {
                int cut = line.IndexOf('|');
                int width = 1;
                if (cut < 0)
                {
                    cut = line.IndexOf('\u2014');
                    if (cut >= 0) { width = 1; }
                }
                if (cut < 0)
                {
                    cut = line.IndexOf(" - ");
                    if (cut >= 0) { width = 3; }
                }
                if (cut <= 0)
                {
                    rows.Add(new NpcStanceRow { Id = line.Trim() });
                }
                else
                {
                    rows.Add(new NpcStanceRow
                    {
                        Id = line.Substring(0, cut).Trim(),
                        Stance = line.Substring(cut + width).Trim(),
                    });
                }
            }
            return rows;
        }

        public static string FormatPartyLine(List<RosterEntry> entries)
        {
            var parts = new List<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) { continue; }
                parts.Add(string.IsNullOrEmpty(entry.Detail)
                    ? entry.Name
                    : entry.Name + " (" + entry.Detail + ")");
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
