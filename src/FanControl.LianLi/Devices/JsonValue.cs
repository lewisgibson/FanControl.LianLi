using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FanControl.LianLi.Devices;

/// <summary>
/// A tiny JSON reader for L-Connect's gzipped config files. The plugin targets
/// netstandard2.0 and ships as a single DLL, so it cannot take a JSON NuGet dependency and
/// <c>System.Text.Json</c> is unavailable; this parses the small, well-formed objects
/// L-Connect writes. Throws <see cref="FormatException"/> on malformed input.
/// </summary>
internal sealed class JsonValue {
    // Dictionary<string,JsonValue> for objects, List<JsonValue> for arrays, string / double /
    // bool for scalars, null for JSON null. The stored runtime type is the discriminator.
    private readonly object? _value;

    private JsonValue(object? value) {
        _value = value;
    }

    /// <summary>Parse a complete JSON document. Throws <see cref="FormatException"/> if malformed.</summary>
    public static JsonValue Parse(string text) {
        if (text is null) {
            throw new ArgumentNullException(nameof(text));
        }

        var parser = new Parser(text);
        JsonValue value = parser.ParseValue();
        parser.SkipWhitespace();
        if (!parser.AtEnd) {
            throw parser.Error("trailing characters after JSON value");
        }

        return value;
    }

    /// <summary>Get an object member by name, or null when absent or this value is not an object.</summary>
    public JsonValue? Member(string name) {
        if (_value is Dictionary<string, JsonValue> map && map.TryGetValue(name, out JsonValue? value)) {
            return value;
        }

        return null;
    }

    /// <summary>Whether this value is an array.</summary>
    public bool IsArray => _value is List<JsonValue>;

    /// <summary>The array elements, or an empty list when this value is not an array.</summary>
    public IReadOnlyList<JsonValue> Elements =>
        _value as List<JsonValue> ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();

    /// <summary>The object member names, or an empty collection when this value is not an object.</summary>
    public IEnumerable<string> MemberNames =>
        _value is Dictionary<string, JsonValue> map ? map.Keys : (IEnumerable<string>)Array.Empty<string>();

    /// <summary>The object member values, or an empty collection when this value is not an object.</summary>
    public IEnumerable<JsonValue> MemberValues =>
        _value is Dictionary<string, JsonValue> map ? map.Values : (IEnumerable<JsonValue>)Array.Empty<JsonValue>();

    /// <summary>This value as a string, or null when it is not a JSON string.</summary>
    public string? AsString() => _value as string;

    /// <summary>This value as an int (number truncated toward zero), or null when it is not a JSON number.</summary>
    public int? AsInt() => _value is double number ? (int)number : (int?)null;

    /// <summary>This value as a double, or null when it is not a JSON number.</summary>
    public double? AsDouble() => _value is double number ? number : (double?)null;

    /// <summary>This value as a bool, or null when it is not a JSON boolean.</summary>
    public bool? AsBool() => _value is bool flag ? flag : (bool?)null;

    private sealed class Parser {
        // Bound on container nesting. L-Connect's documents are shallow; the cap exists only so
        // a crafted deeply-nested file cannot overflow the stack (a StackOverflowException is
        // uncatchable and would crash the host) - it throws a catchable FormatException instead.
        private const int MaxDepth = 64;

        private readonly string _text;
        private int _index;
        private int _depth;

        public Parser(string text) {
            _text = text;
        }

        public bool AtEnd => _index >= _text.Length;

        public FormatException Error(string message) =>
            new FormatException(string.Format(
                CultureInfo.InvariantCulture, "Invalid JSON at position {0}: {1}.", _index, message));

        public void SkipWhitespace() {
            while (_index < _text.Length) {
                char c = _text[_index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                    _index++;
                } else {
                    break;
                }
            }
        }

        public JsonValue ParseValue() {
            SkipWhitespace();
            if (AtEnd) {
                throw Error("unexpected end of input");
            }

            switch (_text[_index]) {
                case '{':
                case '[':
                    if (++_depth > MaxDepth) {
                        throw Error("maximum nesting depth exceeded");
                    }

                    JsonValue container = _text[_index] == '{' ? ParseObject() : ParseArray();
                    _depth--;
                    return container;
                case '"':
                    return new JsonValue(ParseString());
                case 't':
                case 'f':
                    return ParseBoolean();
                case 'n':
                    return ParseNull();
                default:
                    return ParseNumber();
            }
        }

        private JsonValue ParseObject() {
            _index++; // consume '{'
            var map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            SkipWhitespace();
            if (!AtEnd && _text[_index] == '}') {
                _index++;
                return new JsonValue(map);
            }

            while (true) {
                SkipWhitespace();
                if (AtEnd || _text[_index] != '"') {
                    throw Error("expected object key");
                }

                string key = ParseString();
                SkipWhitespace();
                if (AtEnd || _text[_index] != ':') {
                    throw Error("expected ':'");
                }

                _index++; // consume ':'
                map[key] = ParseValue();
                SkipWhitespace();
                if (AtEnd) {
                    throw Error("unterminated object");
                }

                char separator = _text[_index++];
                if (separator == ',') {
                    continue;
                }

                if (separator == '}') {
                    break;
                }

                throw Error("expected ',' or '}'");
            }

            return new JsonValue(map);
        }

        private JsonValue ParseArray() {
            _index++; // consume '['
            var list = new List<JsonValue>();
            SkipWhitespace();
            if (!AtEnd && _text[_index] == ']') {
                _index++;
                return new JsonValue(list);
            }

            while (true) {
                list.Add(ParseValue());
                SkipWhitespace();
                if (AtEnd) {
                    throw Error("unterminated array");
                }

                char separator = _text[_index++];
                if (separator == ',') {
                    continue;
                }

                if (separator == ']') {
                    break;
                }

                throw Error("expected ',' or ']'");
            }

            return new JsonValue(list);
        }

        private string ParseString() {
            _index++; // consume opening quote
            var builder = new StringBuilder();
            while (true) {
                if (AtEnd) {
                    throw Error("unterminated string");
                }

                char c = _text[_index++];
                if (c == '"') {
                    break;
                }

                if (c != '\\') {
                    builder.Append(c);
                    continue;
                }

                if (AtEnd) {
                    throw Error("unterminated escape");
                }

                char escape = _text[_index++];
                switch (escape) {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (_index + 4 > _text.Length) {
                            throw Error("incomplete \\u escape");
                        }

                        builder.Append((char)int.Parse(
                            _text.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _index += 4;
                        break;
                    default:
                        throw Error("invalid escape sequence");
                }
            }

            return builder.ToString();
        }

        private JsonValue ParseNumber() {
            int start = _index;
            while (_index < _text.Length && "+-0123456789.eE".IndexOf(_text[_index]) >= 0) {
                _index++;
            }

            if (_index == start) {
                throw Error("invalid value");
            }

            string token = _text.Substring(start, _index - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) {
                throw Error("invalid number");
            }

            return new JsonValue(number);
        }

        private JsonValue ParseBoolean() {
            if (Match("true")) {
                return new JsonValue(true);
            }

            if (Match("false")) {
                return new JsonValue(false);
            }

            throw Error("invalid literal");
        }

        private JsonValue ParseNull() {
            if (Match("null")) {
                return new JsonValue(null);
            }

            throw Error("invalid literal");
        }

        private bool Match(string literal) {
            if (_index + literal.Length > _text.Length
                || string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0) {
                return false;
            }

            _index += literal.Length;
            return true;
        }
    }
}
