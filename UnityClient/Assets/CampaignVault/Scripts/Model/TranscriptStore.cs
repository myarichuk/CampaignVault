using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// The client's own record of what was played, one JSON-lines file per
    /// campaign (the server keeps the world, not the chat). Only the story is
    /// kept: player lines, narration, voices, rolls and session recaps; tool
    /// activity, the DM's notes and system lines are per-run noise. Appends are
    /// whole turns, so a crash mid-turn loses that turn and nothing else.
    /// </summary>
    public sealed class TranscriptStore
    {
        /// <summary>Segments shown when a campaign's history is reopened.</summary>
        public const int LoadLimit = 160;
        /// <summary>Past this the file is rewritten to its last KeepLines lines on the next load.</summary>
        public const long CompactBytes = 4L * 1024 * 1024;
        public const int KeepLines = 2000;

        private readonly string _dir;

        public TranscriptStore(string dir) { _dir = dir; }

        public string Directory { get { return _dir; } }

        public string PathFor(string slug)
        {
            var sb = new StringBuilder();
            foreach (char c in (slug ?? string.Empty).Trim().ToLowerInvariant())
            {
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' ? c : '_');
            }
            return Path.Combine(_dir, (sb.Length > 0 ? sb.ToString() : "_") + ".jsonl");
        }

        public static bool Persists(TranscriptSegment seg)
        {
            if (seg == null || seg.Streaming || seg.Restored) { return false; }
            switch (seg.Kind)
            {
                case SegmentKind.Player:
                case SegmentKind.Narration:
                case SegmentKind.NpcVoice:
                case SegmentKind.Roll:
                case SegmentKind.Recap:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Appends the segments worth keeping. Never throws: history is a convenience, not state.</summary>
        public bool Append(string slug, IEnumerable<TranscriptSegment> segments)
        {
            if (string.IsNullOrEmpty(slug)) { return false; }
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                if (Persists(seg)) { sb.Append(ToJson(seg).ToJson()).Append('\n'); }
            }
            if (sb.Length == 0) { return true; }
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                File.AppendAllText(PathFor(slug), sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The last limit segments, oldest first, marked Restored. Unreadable lines are skipped.</summary>
        public List<TranscriptSegment> Load(string slug, int limit = LoadLimit)
        {
            var result = new List<TranscriptSegment>();
            if (string.IsNullOrEmpty(slug)) { return result; }
            string path = PathFor(slug);
            string[] lines;
            try
            {
                if (!File.Exists(path)) { return result; }
                lines = File.ReadAllLines(path, Encoding.UTF8);
                if (new FileInfo(path).Length > CompactBytes && lines.Length > KeepLines)
                {
                    var kept = new string[KeepLines];
                    Array.Copy(lines, lines.Length - KeepLines, kept, 0, KeepLines);
                    File.WriteAllLines(path, kept, Encoding.UTF8);
                    lines = kept;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return result;
            }
            for (int i = Math.Max(0, lines.Length - limit); i < lines.Length; i++)
            {
                JsonValue json;
                if (!JsonValue.TryParse(lines[i], out json) || json.Kind != JsonKind.Object) { continue; }
                var seg = FromJson(json);
                if (seg != null) { result.Add(seg); }
            }
            return result;
        }

        public void Delete(string slug)
        {
            try
            {
                string path = PathFor(slug);
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        internal static JsonValue ToJson(TranscriptSegment seg)
        {
            var o = JsonValue.NewObject();
            o.ObjectValue["k"] = JsonValue.FromString(seg.Kind.ToString());
            if (!string.IsNullOrEmpty(seg.Speaker)) { o.ObjectValue["s"] = JsonValue.FromString(seg.Speaker); }
            o.ObjectValue["t"] = JsonValue.FromString(seg.Text ?? string.Empty);
            o.ObjectValue["at"] = JsonValue.FromString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (seg.Roll != null)
            {
                var r = JsonValue.NewObject();
                r.ObjectValue["label"] = JsonValue.FromString(seg.Roll.Label);
                r.ObjectValue["detail"] = JsonValue.FromString(seg.Roll.Detail);
                r.ObjectValue["verdict"] = JsonValue.FromString(seg.Roll.Verdict);
                r.ObjectValue["outcome"] = JsonValue.FromString(seg.Roll.Outcome.ToString());
                r.ObjectValue["total"] = JsonValue.FromNumber(seg.Roll.Total);
                r.ObjectValue["target"] = JsonValue.FromNumber(seg.Roll.Target);
                o.ObjectValue["roll"] = r;
            }
            return o;
        }

        internal static TranscriptSegment FromJson(JsonValue o)
        {
            SegmentKind kind;
            if (!TryEnum(o.GetString("k", string.Empty), out kind)) { return null; }
            var seg = new TranscriptSegment
            {
                Kind = kind,
                Speaker = o.GetString("s", string.Empty),
                Text = o.GetString("t", string.Empty),
                Restored = true,
            };
            var r = o.Get("roll");
            if (r.Kind == JsonKind.Object)
            {
                RollOutcome outcome;
                seg.Roll = new RollInfo
                {
                    Label = r.GetString("label", string.Empty),
                    Detail = r.GetString("detail", string.Empty),
                    Verdict = r.GetString("verdict", string.Empty),
                    Outcome = TryEnum(r.GetString("outcome", string.Empty), out outcome) ? outcome : RollOutcome.Failure,
                    Total = (int)r.GetNumber("total", 0),
                    Target = (int)r.GetNumber("target", 0),
                };
            }
            else if (kind == SegmentKind.Roll)
            {
                return null;
            }
            return seg;
        }

        private static bool TryEnum<T>(string text, out T value) where T : struct
        {
            return Enum.TryParse(text, false, out value) && Enum.IsDefined(typeof(T), value);
        }

        /// <summary>
        /// (player line, the scene that answered it) pairs from saved history,
        /// oldest first, at most max: the storyteller's memory of recent scenes
        /// after a restart. Voices are folded back into the prose.
        /// </summary>
        public static List<KeyValuePair<string, string>> Passages(IList<TranscriptSegment> segments, int max)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            string player = null;
            var scene = new StringBuilder();
            Action flush = delegate
            {
                if (player != null && scene.Length > 0) { pairs.Add(new KeyValuePair<string, string>(player, scene.ToString().Trim())); }
                scene.Length = 0;
            };
            foreach (var seg in segments)
            {
                switch (seg.Kind)
                {
                    case SegmentKind.Player:
                        flush();
                        player = seg.Text;
                        break;
                    case SegmentKind.Narration:
                        if (player != null) { scene.Append(seg.Text).Append("\n\n"); }
                        break;
                    case SegmentKind.NpcVoice:
                        if (player != null) { scene.Append(seg.Speaker).Append(": “").Append(seg.Text).Append("”\n\n"); }
                        break;
                    case SegmentKind.Recap:
                        flush();
                        player = null;
                        break;
                }
            }
            flush();
            if (pairs.Count > max) { pairs.RemoveRange(0, pairs.Count - max); }
            return pairs;
        }
    }
}
