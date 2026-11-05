using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// Why a JSON document was rejected. Kept separate from <see cref="ErrorCodes"/> so the
/// manifest layer can map parser failures to manifest-specific codes.
/// </summary>
internal enum JsonError
{
    None = 0,
    Empty,
    UnexpectedCharacter,
    UnexpectedEnd,
    TrailingContent,
    DuplicateKey,
    TooDeep,
    TooLarge,
    InvalidEscape,
    InvalidNumber,
    UnterminatedString,
}

internal sealed class JsonException : Exception
{
    internal JsonException(JsonError error, int offset, string message)
        : base(message)
    {
        Error = error;
        Offset = offset;
    }

    internal JsonError Error { get; }

    internal int Offset { get; }
}

internal enum JsonKind
{
    Null = 0,
    Bool,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// Minimal immutable-ish JSON tree. Only what a manifest needs: object members in document
/// order (so duplicate-key detection and deterministic diagnostics are possible), arrays,
/// strings, numbers (kept as raw text), booleans and null.
/// </summary>
internal sealed class JsonValue
{
    private JsonValue(JsonKind kind)
    {
        Kind = kind;
    }

    internal JsonKind Kind { get; }

    internal bool BoolValue { get; private set; }

    internal string StringValue { get; private set; } = "";

    internal string RawNumber { get; private set; } = "";

    internal List<JsonValue> Items { get; } = new();

    internal List<KeyValuePair<string, JsonValue>> Members { get; } = new();

    internal int Count => Kind == JsonKind.Array ? Items.Count : Members.Count;

    internal static JsonValue NewNull() => new(JsonKind.Null);

    internal static JsonValue NewBool(bool value) => new(JsonKind.Bool) { BoolValue = value };

    internal static JsonValue NewString(string value) => new(JsonKind.String) { StringValue = value };

    internal static JsonValue NewNumber(string raw) => new(JsonKind.Number) { RawNumber = raw };

    internal static JsonValue NewArray() => new(JsonKind.Array);

    internal static JsonValue NewObject() => new(JsonKind.Object);

    /// <summary>Object member lookup; the last duplicate would win, but duplicates are rejected earlier.</summary>
    internal JsonValue? Get(string key)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (string.Equals(Members[i].Key, key, StringComparison.Ordinal))
            {
                return Members[i].Value;
            }
        }
        return null;
    }

    internal bool Has(string key) => Get(key) != null;

    internal int TryGetInt(int fallback)
    {
        if (Kind != JsonKind.Number)
        {
            return fallback;
        }
        int value;
        return int.TryParse(RawNumber, out value) ? value : fallback;
    }
}

/// <summary>
/// Strict JSON parser (RFC 8259 subset: no comments, no trailing commas, no NaN/Infinity).
/// Limits are explicit because the input is untrusted mod content.
/// </summary>
internal static class JsonParser
{
    internal const int DefaultMaxDepth = 16;
    internal const int DefaultMaxLength = 256 * 1024;

    internal static JsonValue Parse(string text, int maxDepth = DefaultMaxDepth, int maxLength = DefaultMaxLength)
    {
        if (text.Length > maxLength)
        {
            throw new JsonException(JsonError.TooLarge, 0, "document is larger than " + maxLength + " characters");
        }
        var p = new Cursor(text, maxDepth);
        p.SkipWhitespace();
        if (p.AtEnd)
        {
            throw new JsonException(JsonError.Empty, 0, "document is empty");
        }
        var root = p.ParseValue(0);
        p.SkipWhitespace();
        if (!p.AtEnd)
        {
            throw new JsonException(JsonError.TrailingContent, p.Offset, "unexpected content after the root value");
        }
        return root;
    }

    private sealed class Cursor
    {
        private readonly string _text;
        private readonly int _maxDepth;

        internal Cursor(string text, int maxDepth)
        {
            _text = text;
            _maxDepth = maxDepth;
        }

        internal int Offset { get; private set; }

        internal bool AtEnd => Offset >= _text.Length;

        internal void SkipWhitespace()
        {
            while (Offset < _text.Length)
            {
                var c = _text[Offset];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    Offset++;
                    continue;
                }
                break;
            }
        }

        private JsonException Fail(JsonError error, string message) => new(error, Offset, message);

        internal JsonValue ParseValue(int depth)
        {
            if (depth > _maxDepth)
            {
                throw Fail(JsonError.TooDeep, "nesting deeper than " + _maxDepth);
            }
            if (AtEnd)
            {
                throw Fail(JsonError.UnexpectedEnd, "unexpected end of document");
            }
            var c = _text[Offset];
            switch (c)
            {
                case '{':
                    return ParseObject(depth);
                case '[':
                    return ParseArray(depth);
                case '"':
                    return JsonValue.NewString(ParseString());
                case 't':
                    Expect("true");
                    return JsonValue.NewBool(true);
                case 'f':
                    Expect("false");
                    return JsonValue.NewBool(false);
                case 'n':
                    Expect("null");
                    return JsonValue.NewNull();
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ParseNumber();
                    }
                    throw Fail(JsonError.UnexpectedCharacter, "unexpected character '" + c + "'");
            }
        }

        private void Expect(string literal)
        {
            if (Offset + literal.Length > _text.Length)
            {
                throw Fail(JsonError.UnexpectedEnd, "expected '" + literal + "'");
            }
            for (var i = 0; i < literal.Length; i++)
            {
                if (_text[Offset + i] != literal[i])
                {
                    throw Fail(JsonError.UnexpectedCharacter, "expected '" + literal + "'");
                }
            }
            Offset += literal.Length;
        }

        private JsonValue ParseObject(int depth)
        {
            var result = JsonValue.NewObject();
            Offset++; // '{'
            SkipWhitespace();
            if (!AtEnd && _text[Offset] == '}')
            {
                Offset++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Fail(JsonError.UnexpectedEnd, "unterminated object");
                }
                if (_text[Offset] != '"')
                {
                    throw Fail(JsonError.UnexpectedCharacter, "object keys must be strings");
                }
                var keyOffset = Offset;
                var key = ParseString();
                for (var i = 0; i < result.Members.Count; i++)
                {
                    if (string.Equals(result.Members[i].Key, key, StringComparison.Ordinal))
                    {
                        Offset = keyOffset;
                        throw Fail(JsonError.DuplicateKey, "duplicate key \"" + key + "\"");
                    }
                }
                SkipWhitespace();
                if (AtEnd || _text[Offset] != ':')
                {
                    throw Fail(JsonError.UnexpectedCharacter, "expected ':' after an object key");
                }
                Offset++;
                SkipWhitespace();
                var value = ParseValue(depth + 1);
                result.Members.Add(new KeyValuePair<string, JsonValue>(key, value));
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Fail(JsonError.UnexpectedEnd, "unterminated object");
                }
                if (_text[Offset] == ',')
                {
                    Offset++;
                    continue;
                }
                if (_text[Offset] == '}')
                {
                    Offset++;
                    return result;
                }
                throw Fail(JsonError.UnexpectedCharacter, "expected ',' or '}' in object");
            }
        }

        private JsonValue ParseArray(int depth)
        {
            var result = JsonValue.NewArray();
            Offset++; // '['
            SkipWhitespace();
            if (!AtEnd && _text[Offset] == ']')
            {
                Offset++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                result.Items.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Fail(JsonError.UnexpectedEnd, "unterminated array");
                }
                if (_text[Offset] == ',')
                {
                    Offset++;
                    continue;
                }
                if (_text[Offset] == ']')
                {
                    Offset++;
                    return result;
                }
                throw Fail(JsonError.UnexpectedCharacter, "expected ',' or ']' in array");
            }
        }

        private string ParseString()
        {
            Offset++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Fail(JsonError.UnterminatedString, "unterminated string");
                }
                var c = _text[Offset];
                if (c == '"')
                {
                    Offset++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    Offset++;
                    if (AtEnd)
                    {
                        throw Fail(JsonError.UnterminatedString, "unterminated escape sequence");
                    }
                    var esc = _text[Offset];
                    Offset++;
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
                            sb.Append(ParseUnicodeEscape());
                            break;
                        default:
                            throw Fail(JsonError.InvalidEscape, "invalid escape '\\" + esc + "'");
                    }
                    continue;
                }
                if (c < 0x20)
                {
                    throw Fail(JsonError.InvalidEscape, "unescaped control character U+" + ((int)c).ToString("X4"));
                }
                sb.Append(c);
                Offset++;
            }
        }

        private char ParseUnicodeEscape()
        {
            if (Offset + 4 > _text.Length)
            {
                throw Fail(JsonError.UnexpectedEnd, "truncated \\u escape");
            }
            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                var c = _text[Offset + i];
                int digit;
                if (c >= '0' && c <= '9') { digit = c - '0'; }
                else if (c >= 'a' && c <= 'f') { digit = c - 'a' + 10; }
                else if (c >= 'A' && c <= 'F') { digit = c - 'A' + 10; }
                else { throw Fail(JsonError.InvalidEscape, "invalid hex digit in \\u escape"); }
                value = (value << 4) + digit;
            }
            Offset += 4;
            return (char)value;
        }

        private JsonValue ParseNumber()
        {
            var start = Offset;
            if (!AtEnd && _text[Offset] == '-')
            {
                Offset++;
            }
            var digits = 0;
            while (!AtEnd && _text[Offset] >= '0' && _text[Offset] <= '9')
            {
                Offset++;
                digits++;
            }
            if (digits == 0)
            {
                throw Fail(JsonError.InvalidNumber, "expected a digit");
            }
            if (!AtEnd && _text[Offset] == '.')
            {
                Offset++;
                var frac = 0;
                while (!AtEnd && _text[Offset] >= '0' && _text[Offset] <= '9')
                {
                    Offset++;
                    frac++;
                }
                if (frac == 0)
                {
                    throw Fail(JsonError.InvalidNumber, "expected a digit after '.'");
                }
            }
            if (!AtEnd && (_text[Offset] == 'e' || _text[Offset] == 'E'))
            {
                Offset++;
                if (!AtEnd && (_text[Offset] == '+' || _text[Offset] == '-'))
                {
                    Offset++;
                }
                var exp = 0;
                while (!AtEnd && _text[Offset] >= '0' && _text[Offset] <= '9')
                {
                    Offset++;
                    exp++;
                }
                if (exp == 0)
                {
                    throw Fail(JsonError.InvalidNumber, "expected a digit in the exponent");
                }
            }
            return JsonValue.NewNumber(_text.Substring(start, Offset - start));
        }
    }
}
