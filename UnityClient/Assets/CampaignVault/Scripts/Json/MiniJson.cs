using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CampaignVault.UnityClient.Json
{
    /// <summary>
    /// Minimal strict JSON DOM: parser plus writer, no reflection, no codegen.
    /// Exists so the Unity client makes zero package-registry round trips and so
    /// hostile payloads face a parser with a hard nesting cap, not a full serializer.
    /// Unity-compatible language level only (no records, no file-scoped namespaces).
    /// </summary>
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    public sealed class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }

    public sealed class JsonValue
    {
        public const int MaxDepth = 64;
        public const int MaxDocumentChars = 4 * 1024 * 1024;

        public JsonKind Kind;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public List<JsonValue> ArrayValue;
        public Dictionary<string, JsonValue> ObjectValue;

        public static readonly JsonValue Null = new JsonValue { Kind = JsonKind.Null };

        public static JsonValue FromBool(bool v) { return new JsonValue { Kind = JsonKind.Bool, BoolValue = v }; }
        public static JsonValue FromNumber(double v) { return new JsonValue { Kind = JsonKind.Number, NumberValue = v }; }
        public static JsonValue FromString(string v) { return new JsonValue { Kind = JsonKind.String, StringValue = v ?? string.Empty }; }
        public static JsonValue NewArray() { return new JsonValue { Kind = JsonKind.Array, ArrayValue = new List<JsonValue>() }; }
        public static JsonValue NewObject() { return new JsonValue { Kind = JsonKind.Object, ObjectValue = new Dictionary<string, JsonValue>() }; }

        public bool IsNull { get { return Kind == JsonKind.Null; } }

        public JsonValue Get(string key)
        {
            if (Kind == JsonKind.Object && ObjectValue != null && ObjectValue.TryGetValue(key, out var v))
            {
                return v;
            }
            return Null;
        }

        public string GetString(string key, string fallback)
        {
            var v = Get(key);
            if (v.Kind == JsonKind.String) { return v.StringValue; }
            if (v.Kind == JsonKind.Number) { return v.NumberValue.ToString(CultureInfo.InvariantCulture); }
            if (v.Kind == JsonKind.Bool) { return v.BoolValue ? "true" : "false"; }
            return fallback;
        }

        public string GetStringAny(string[] keys, string fallback)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                var v = Get(keys[i]);
                if (v.Kind == JsonKind.String) { return v.StringValue; }
                if (v.Kind == JsonKind.Number) { return v.NumberValue.ToString(CultureInfo.InvariantCulture); }
                if (v.Kind == JsonKind.Bool) { return v.BoolValue ? "true" : "false"; }
            }
            return fallback;
        }

        public double GetNumber(string key, double fallback)
        {
            var v = Get(key);
            if (v.Kind == JsonKind.Number) { return v.NumberValue; }
            if (v.Kind == JsonKind.String)
            {
                double parsed;
                if (double.TryParse(v.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) { return parsed; }
            }
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            var v = Get(key);
            if (v.Kind == JsonKind.Bool) { return v.BoolValue; }
            return fallback;
        }

        public List<JsonValue> GetArray(string key)
        {
            var v = Get(key);
            if (v.Kind == JsonKind.Array && v.ArrayValue != null) { return v.ArrayValue; }
            return new List<JsonValue>();
        }

        public static JsonValue Parse(string text)
        {
            if (text == null) { throw new JsonParseException("null document"); }
            if (text.Length > MaxDocumentChars) { throw new JsonParseException("document too large"); }
            var parser = new Parser(text);
            var value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd()) { throw new JsonParseException("trailing characters after document"); }
            return value;
        }

        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (JsonParseException)
            {
                value = Null;
                return false;
            }
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            WriteTo(sb);
            return sb.ToString();
        }

        private void WriteTo(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(BoolValue ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteQuoted(sb, StringValue ?? string.Empty);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < ArrayValue.Count; i++)
                    {
                        if (i > 0) { sb.Append(','); }
                        ArrayValue[i].WriteTo(sb);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in ObjectValue)
                    {
                        if (!first) { sb.Append(','); }
                        first = false;
                        WriteQuoted(sb, kv.Key);
                        sb.Append(':');
                        kv.Value.WriteTo(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        public static void WriteQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); }
                        else { sb.Append(c); }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;

            public Parser(string text) { _text = text; _pos = 0; }

            public bool AtEnd() { return _pos >= _text.Length; }

            public void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { _pos++; }
                    else { break; }
                }
            }

            public JsonValue ParseValue(int depth)
            {
                if (depth > MaxDepth) { throw new JsonParseException("nesting too deep"); }
                SkipWhitespace();
                if (AtEnd()) { throw new JsonParseException("unexpected end of document"); }
                char c = _text[_pos];
                if (c == '{') { return ParseObject(depth); }
                if (c == '[') { return ParseArray(depth); }
                if (c == '"') { return FromString(ParseString()); }
                if (c == 't') { Expect("true"); return FromBool(true); }
                if (c == 'f') { Expect("false"); return FromBool(false); }
                if (c == 'n') { Expect("null"); return Null; }
                return ParseNumber();
            }

            private JsonValue ParseObject(int depth)
            {
                _pos++; // {
                var obj = NewObject();
                SkipWhitespace();
                if (AtEnd()) { throw new JsonParseException("unterminated object"); }
                if (_text[_pos] == '}') { _pos++; return obj; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd() || _text[_pos] != '"') { throw new JsonParseException("expected string key"); }
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd() || _text[_pos] != ':') { throw new JsonParseException("expected ':'"); }
                    _pos++;
                    obj.ObjectValue[key] = ParseValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd()) { throw new JsonParseException("unterminated object"); }
                    char next = _text[_pos++];
                    if (next == '}') { return obj; }
                    if (next != ',') { throw new JsonParseException("expected ',' or '}'"); }
                }
            }

            private JsonValue ParseArray(int depth)
            {
                _pos++; // [
                var arr = NewArray();
                SkipWhitespace();
                if (AtEnd()) { throw new JsonParseException("unterminated array"); }
                if (_text[_pos] == ']') { _pos++; return arr; }
                while (true)
                {
                    arr.ArrayValue.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd()) { throw new JsonParseException("unterminated array"); }
                    char next = _text[_pos++];
                    if (next == ']') { return arr; }
                    if (next != ',') { throw new JsonParseException("expected ',' or ']'"); }
                }
            }

            private string ParseString()
            {
                _pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _text.Length) { throw new JsonParseException("unterminated string"); }
                    char c = _text[_pos++];
                    if (c == '"') { return sb.ToString(); }
                    if (c == '\\')
                    {
                        if (_pos >= _text.Length) { throw new JsonParseException("bad escape"); }
                        char e = _text[_pos++];
                        if (e == '"') { sb.Append('"'); }
                        else if (e == '\\') { sb.Append('\\'); }
                        else if (e == '/') { sb.Append('/'); }
                        else if (e == 'b') { sb.Append('\b'); }
                        else if (e == 'f') { sb.Append('\f'); }
                        else if (e == 'n') { sb.Append('\n'); }
                        else if (e == 'r') { sb.Append('\r'); }
                        else if (e == 't') { sb.Append('\t'); }
                        else if (e == 'u')
                        {
                            if (_pos + 4 > _text.Length) { throw new JsonParseException("bad unicode escape"); }
                            string hex = _text.Substring(_pos, 4);
                            int code;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                throw new JsonParseException("bad unicode escape");
                            }
                            _pos += 4;
                            sb.Append((char)code);
                        }
                        else { throw new JsonParseException("bad escape"); }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            private JsonValue ParseNumber()
            {
                int start = _pos;
                if (_pos < _text.Length && (_text[_pos] == '-' || _text[_pos] == '+')) { _pos++; }
                bool digits = false;
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' || _text[_pos] == 'e' || _text[_pos] == 'E' || _text[_pos] == '+' || _text[_pos] == '-'))
                {
                    if (char.IsDigit(_text[_pos])) { digits = true; }
                    _pos++;
                }
                if (!digits) { throw new JsonParseException("invalid value"); }
                double number;
                if (!double.TryParse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    throw new JsonParseException("invalid number");
                }
                return FromNumber(number);
            }

            private void Expect(string word)
            {
                if (_pos + word.Length > _text.Length || _text.Substring(_pos, word.Length) != word)
                {
                    throw new JsonParseException("invalid literal");
                }
                _pos += word.Length;
            }
        }
    }
}
