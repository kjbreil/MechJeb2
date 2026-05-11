// Minimal strict JSON DOM + parser + writer for MechJeb's MCP server.
//
// Why not Newtonsoft.Json: KSP 1.12 does not bundle it (verified on macOS install).
// Why not SimpleJSON: also a dependency to vendor; this footprint is smaller
// and tailored to JSON-RPC 2.0's needs.
//
// Properties:
//   * Strict RFC 8259 JSON: no comments, no trailing commas, no unquoted keys.
//   * Max nesting depth enforced (default 64) to defend against pathological input.
//   * NaN/+Inf/-Inf double values serialize as JSON null (RFC compliant).
//   * UTF-16 surrogate pairs handled in \uXXXX\uYYYY string escapes.
//   * No reflection, no $type, no polymorphic deserialization. There is no
//     deserialize-into-typed-object path: callers walk the DOM explicitly.
//     This is the security posture we want (cf. plan Refinements G1).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MuMech.Mcp
{
    public enum JsonType { Null, Bool, Number, String, Array, Object }

    public abstract class JsonValue
    {
        public abstract JsonType Type { get; }

        public bool IsNull => Type == JsonType.Null;
        public bool IsObject => Type == JsonType.Object;
        public bool IsArray => Type == JsonType.Array;

        public virtual bool AsBool => throw new InvalidCastException($"Cannot read {Type} as bool");
        public virtual double AsNumber => throw new InvalidCastException($"Cannot read {Type} as number");
        public virtual long AsInt => (long)AsNumber;
        public virtual string AsString => throw new InvalidCastException($"Cannot read {Type} as string");
        public virtual JsonObject AsObject => throw new InvalidCastException($"Cannot read {Type} as object");
        public virtual JsonArray AsArray => throw new InvalidCastException($"Cannot read {Type} as array");

        public virtual JsonValue this[string key]
        {
            get => throw new InvalidCastException($"Indexing by string requires object, got {Type}");
            set => throw new InvalidCastException($"Indexing by string requires object, got {Type}");
        }

        public virtual JsonValue this[int index]
        {
            get => throw new InvalidCastException($"Indexing by int requires array, got {Type}");
            set => throw new InvalidCastException($"Indexing by int requires array, got {Type}");
        }

        public override string ToString() => Json.Stringify(this);

        public static implicit operator JsonValue(string s) => s == null ? (JsonValue)JsonNull.Instance : new JsonString(s);
        public static implicit operator JsonValue(bool b) => new JsonBool(b);
        public static implicit operator JsonValue(int i) => new JsonNumber(i);
        public static implicit operator JsonValue(long l) => new JsonNumber(l);
        public static implicit operator JsonValue(double d) => new JsonNumber(d);
    }

    public sealed class JsonNull : JsonValue
    {
        public static readonly JsonNull Instance = new JsonNull();
        private JsonNull() { }
        public override JsonType Type => JsonType.Null;
    }

    public sealed class JsonBool : JsonValue
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);
        private readonly bool _value;
        public JsonBool(bool value) { _value = value; }
        public override JsonType Type => JsonType.Bool;
        public override bool AsBool => _value;
    }

    public sealed class JsonNumber : JsonValue
    {
        private readonly double _value;
        public JsonNumber(double value) { _value = value; }
        public JsonNumber(long value) { _value = value; }
        public override JsonType Type => JsonType.Number;
        public override double AsNumber => _value;
    }

    public sealed class JsonString : JsonValue
    {
        private readonly string _value;
        public JsonString(string value) { _value = value ?? throw new ArgumentNullException(nameof(value)); }
        public override JsonType Type => JsonType.String;
        public override string AsString => _value;
    }

    public sealed class JsonArray : JsonValue, IEnumerable<JsonValue>
    {
        private readonly List<JsonValue> _items;
        public JsonArray() { _items = new List<JsonValue>(); }
        public JsonArray(int capacity) { _items = new List<JsonValue>(capacity); }
        public override JsonType Type => JsonType.Array;
        public override JsonArray AsArray => this;
        public int Count => _items.Count;

        public override JsonValue this[int index]
        {
            get => _items[index];
            set => _items[index] = value ?? JsonNull.Instance;
        }

        public JsonArray Add(JsonValue value) { _items.Add(value ?? JsonNull.Instance); return this; }
        public IEnumerator<JsonValue> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    public sealed class JsonObject : JsonValue, IEnumerable<KeyValuePair<string, JsonValue>>
    {
        // Insertion-ordered. JSON-RPC field order matters for some readers.
        private readonly List<string> _keys;
        private readonly Dictionary<string, JsonValue> _entries;

        public JsonObject()
        {
            _keys = new List<string>();
            _entries = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        }

        public override JsonType Type => JsonType.Object;
        public override JsonObject AsObject => this;
        public int Count => _keys.Count;

        public IEnumerable<string> Keys => _keys;

        public override JsonValue this[string key]
        {
            get => _entries.TryGetValue(key, out JsonValue v) ? v : JsonNull.Instance;
            set => Set(key, value);
        }

        public bool ContainsKey(string key) => _entries.ContainsKey(key);

        public bool TryGet(string key, out JsonValue value) => _entries.TryGetValue(key, out value);

        public JsonObject Set(string key, JsonValue value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            JsonValue v = value ?? JsonNull.Instance;
            if (!_entries.ContainsKey(key)) _keys.Add(key);
            _entries[key] = v;
            return this;
        }

        public bool Remove(string key)
        {
            if (!_entries.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator()
        {
            foreach (string k in _keys) yield return new KeyValuePair<string, JsonValue>(k, _entries[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class JsonException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public JsonException(string message, int line, int column)
            : base($"{message} (line {line}, column {column})")
        {
            Line = line;
            Column = column;
        }
    }

    public static class Json
    {
        public const int DefaultMaxDepth = 64;

        public static JsonValue Parse(string input, int maxDepth = DefaultMaxDepth)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var p = new JsonParser(input, maxDepth);
            JsonValue value = p.ParseValue();
            p.SkipWhitespace();
            if (!p.IsAtEnd) throw new JsonException("Trailing content after root value", p.Line, p.Column);
            return value;
        }

        public static string Stringify(JsonValue value, bool pretty = false)
        {
            var sb = new StringBuilder(256);
            var w = new JsonWriter(sb, pretty);
            w.Write(value);
            return sb.ToString();
        }

        public static JsonObject Obj() => new JsonObject();
        public static JsonArray Arr() => new JsonArray();
    }

    internal sealed class JsonParser
    {
        private readonly string _src;
        private readonly int _maxDepth;
        private int _pos;
        private int _depth;
        public int Line { get; private set; } = 1;
        public int Column { get; private set; } = 1;

        public JsonParser(string src, int maxDepth)
        {
            _src = src;
            _maxDepth = maxDepth;
        }

        public bool IsAtEnd => _pos >= _src.Length;

        public JsonValue ParseValue()
        {
            SkipWhitespace();
            if (IsAtEnd) throw new JsonException("Unexpected end of input", Line, Column);
            char c = _src[_pos];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return new JsonString(ParseString());
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
            if (c == 't' || c == 'f') return ParseBool();
            if (c == 'n') return ParseNull();
            throw new JsonException($"Unexpected character '{c}'", Line, Column);
        }

        private JsonObject ParseObject()
        {
            Enter();
            Consume('{');
            var obj = new JsonObject();
            SkipWhitespace();
            if (Peek() == '}') { Advance(); Leave(); return obj; }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw new JsonException("Expected string key in object", Line, Column);
                string key = ParseString();
                SkipWhitespace();
                if (Peek() != ':') throw new JsonException("Expected ':' after object key", Line, Column);
                Advance();
                JsonValue value = ParseValue();
                obj.Set(key, value);
                SkipWhitespace();
                char next = Peek();
                if (next == ',') { Advance(); continue; }
                if (next == '}') { Advance(); break; }
                throw new JsonException($"Expected ',' or '}}' in object, got '{next}'", Line, Column);
            }
            Leave();
            return obj;
        }

        private JsonArray ParseArray()
        {
            Enter();
            Consume('[');
            var arr = new JsonArray();
            SkipWhitespace();
            if (Peek() == ']') { Advance(); Leave(); return arr; }
            while (true)
            {
                arr.Add(ParseValue());
                SkipWhitespace();
                char next = Peek();
                if (next == ',') { Advance(); continue; }
                if (next == ']') { Advance(); break; }
                throw new JsonException($"Expected ',' or ']' in array, got '{next}'", Line, Column);
            }
            Leave();
            return arr;
        }

        private string ParseString()
        {
            Consume('"');
            var sb = new StringBuilder();
            while (!IsAtEnd)
            {
                char c = _src[_pos];
                if (c == '"') { Advance(); return sb.ToString(); }
                if (c == '\\')
                {
                    Advance();
                    if (IsAtEnd) throw new JsonException("Unterminated escape", Line, Column);
                    char esc = _src[_pos]; Advance();
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            int hi = ParseHex4();
                            if (hi >= 0xD800 && hi <= 0xDBFF) // surrogate pair
                            {
                                if (_pos + 1 >= _src.Length || _src[_pos] != '\\' || _src[_pos + 1] != 'u')
                                    throw new JsonException("Invalid surrogate pair: missing low surrogate", Line, Column);
                                Advance(); Advance();
                                int lo = ParseHex4();
                                if (lo < 0xDC00 || lo > 0xDFFF) throw new JsonException("Invalid low surrogate", Line, Column);
                                int cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                                sb.Append(char.ConvertFromUtf32(cp));
                            }
                            else sb.Append((char)hi);
                            break;
                        default: throw new JsonException($"Invalid escape '\\{esc}'", Line, Column);
                    }
                }
                else if (c < 0x20) throw new JsonException("Unescaped control character in string", Line, Column);
                else if (c >= 0xD800 && c <= 0xDBFF)
                {
                    // High surrogate — must be immediately followed by a low surrogate
                    // (the C# string layer may have decoded a valid UTF-8 4-byte sequence
                    // into a UTF-16 pair). Lone surrogates are rejected per RFC 8259 §8.2.
                    sb.Append(c); Advance();
                    if (IsAtEnd) throw new JsonException("Lone high surrogate at end of string", Line, Column);
                    char low = _src[_pos];
                    if (low < 0xDC00 || low > 0xDFFF)
                        throw new JsonException("High surrogate not followed by low surrogate", Line, Column);
                    sb.Append(low); Advance();
                }
                else if (c >= 0xDC00 && c <= 0xDFFF)
                    throw new JsonException("Lone low surrogate in string", Line, Column);
                else { sb.Append(c); Advance(); }
            }
            throw new JsonException("Unterminated string", Line, Column);
        }

        private int ParseHex4()
        {
            if (_pos + 4 > _src.Length) throw new JsonException("Truncated \\uXXXX escape", Line, Column);
            int v = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = _src[_pos]; Advance();
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = 10 + (c - 'a');
                else if (c >= 'A' && c <= 'F') d = 10 + (c - 'A');
                else throw new JsonException($"Invalid hex digit '{c}' in \\uXXXX escape", Line, Column);
                v = (v << 4) | d;
            }
            return v;
        }

        private JsonValue ParseNumber()
        {
            int start = _pos;
            if (Peek() == '-') Advance();
            // integer part — leading zeros are forbidden per RFC 8259 §6
            if (Peek() == '0')
            {
                Advance();
                if (!IsAtEnd && _src[_pos] >= '0' && _src[_pos] <= '9')
                    throw new JsonException("Leading zeros not allowed in number", Line, Column);
            }
            else if (Peek() >= '1' && Peek() <= '9')
            {
                while (!IsAtEnd && _src[_pos] >= '0' && _src[_pos] <= '9') Advance();
            }
            else throw new JsonException("Invalid number", Line, Column);
            // fraction
            if (!IsAtEnd && _src[_pos] == '.')
            {
                Advance();
                if (IsAtEnd || _src[_pos] < '0' || _src[_pos] > '9') throw new JsonException("Invalid fraction", Line, Column);
                while (!IsAtEnd && _src[_pos] >= '0' && _src[_pos] <= '9') Advance();
            }
            // exponent
            if (!IsAtEnd && (_src[_pos] == 'e' || _src[_pos] == 'E'))
            {
                Advance();
                if (!IsAtEnd && (_src[_pos] == '+' || _src[_pos] == '-')) Advance();
                if (IsAtEnd || _src[_pos] < '0' || _src[_pos] > '9') throw new JsonException("Invalid exponent", Line, Column);
                while (!IsAtEnd && _src[_pos] >= '0' && _src[_pos] <= '9') Advance();
            }
            string text = _src.Substring(start, _pos - start);
            double d = double.Parse(text, CultureInfo.InvariantCulture);
            return new JsonNumber(d);
        }

        private JsonValue ParseBool()
        {
            if (Match("true")) return JsonBool.True;
            if (Match("false")) return JsonBool.False;
            throw new JsonException("Invalid bool literal", Line, Column);
        }

        private JsonValue ParseNull()
        {
            if (Match("null")) return JsonNull.Instance;
            throw new JsonException("Invalid null literal", Line, Column);
        }

        private bool Match(string literal)
        {
            if (_pos + literal.Length > _src.Length) return false;
            for (int i = 0; i < literal.Length; i++) if (_src[_pos + i] != literal[i]) return false;
            for (int i = 0; i < literal.Length; i++) Advance();
            return true;
        }

        public void SkipWhitespace()
        {
            while (!IsAtEnd)
            {
                char c = _src[_pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') Advance();
                else return;
            }
        }

        private char Peek() => IsAtEnd ? '\0' : _src[_pos];

        private void Consume(char expected)
        {
            if (IsAtEnd || _src[_pos] != expected) throw new JsonException($"Expected '{expected}'", Line, Column);
            Advance();
        }

        private void Advance()
        {
            if (IsAtEnd) return;
            if (_src[_pos] == '\n') { Line++; Column = 1; }
            else Column++;
            _pos++;
        }

        private void Enter()
        {
            _depth++;
            if (_depth > _maxDepth) throw new JsonException($"Max nesting depth {_maxDepth} exceeded", Line, Column);
        }

        private void Leave() { _depth--; }
    }

    internal struct JsonWriter
    {
        private readonly StringBuilder _sb;
        private readonly bool _pretty;
        private int _indent;

        public JsonWriter(StringBuilder sb, bool pretty)
        {
            _sb = sb;
            _pretty = pretty;
            _indent = 0;
        }

        public void Write(JsonValue value)
        {
            if (value == null) { _sb.Append("null"); return; }
            switch (value.Type)
            {
                case JsonType.Null: _sb.Append("null"); return;
                case JsonType.Bool: _sb.Append(value.AsBool ? "true" : "false"); return;
                case JsonType.Number: WriteNumber(value.AsNumber); return;
                case JsonType.String: WriteString(value.AsString); return;
                case JsonType.Array: WriteArray((JsonArray)value); return;
                case JsonType.Object: WriteObject((JsonObject)value); return;
            }
        }

        private void WriteNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { _sb.Append("null"); return; }
            // Integer fast-path for clean ints (net48 has no double.IsNegative).
            if (d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d)
            {
                _sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                return;
            }
            _sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        private void WriteArray(JsonArray arr)
        {
            if (arr.Count == 0) { _sb.Append("[]"); return; }
            _sb.Append('[');
            if (_pretty) { _indent++; NewLine(); }
            bool first = true;
            foreach (JsonValue v in arr)
            {
                if (!first) { _sb.Append(','); if (_pretty) NewLine(); }
                first = false;
                Write(v);
            }
            if (_pretty) { _indent--; NewLine(); }
            _sb.Append(']');
        }

        private void WriteObject(JsonObject obj)
        {
            if (obj.Count == 0) { _sb.Append("{}"); return; }
            _sb.Append('{');
            if (_pretty) { _indent++; NewLine(); }
            bool first = true;
            foreach (KeyValuePair<string, JsonValue> kv in obj)
            {
                if (!first) { _sb.Append(','); if (_pretty) NewLine(); }
                first = false;
                WriteString(kv.Key);
                _sb.Append(':');
                if (_pretty) _sb.Append(' ');
                Write(kv.Value);
            }
            if (_pretty) { _indent--; NewLine(); }
            _sb.Append('}');
        }

        private void NewLine()
        {
            _sb.Append('\n');
            for (int i = 0; i < _indent; i++) _sb.Append("  ");
        }
    }
}
