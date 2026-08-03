using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using HandlebarsDotNet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mockifyr.Templating;

/// <summary>
/// Mockifyr's format / math / array / string Handlebars helpers (G2g), built on the jknack
/// Handlebars built-ins: <c>math</c>, <c>numberFormat</c>, <c>size</c>,
/// <c>join</c>, <c>substring</c>, <c>replace</c>, <c>upper</c>, <c>lower</c>, <c>capitalize</c>,
/// <c>trim</c>. All behaviors are verified by the differential suite — see docs/parity/g2-response.md. The
/// modulo/power operators, and helpers outside the jknack built-in set (abs/round/split/…), are
/// deferred (the oracle rejects them, so there is nothing to validate against).
/// </summary>
internal static class FormatHelpers
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static void Register(IHandlebars handlebars)
    {
        handlebars.RegisterHelper("math", (_, arguments) => Math(arguments));
        handlebars.RegisterHelper("numberFormat", (_, arguments) => NumberFormat(arguments));
        handlebars.RegisterHelper("size", (_, arguments) => Size(arguments));
        handlebars.RegisterHelper("join", (_, arguments) => Join(arguments));
        handlebars.RegisterHelper("substring", (_, arguments) => Substring(arguments));
        handlebars.RegisterHelper("replace", (_, arguments) => Replace(arguments));
        handlebars.RegisterHelper("upper", (_, arguments) => Str(arguments, 0).ToUpperInvariant());
        handlebars.RegisterHelper("lower", (_, arguments) => Str(arguments, 0).ToLowerInvariant());
        handlebars.RegisterHelper("capitalize", (_, arguments) => Capitalize(Str(arguments, 0)));
        handlebars.RegisterHelper("trim", (_, arguments) => Str(arguments, 0).Trim());
        handlebars.RegisterHelper("base64", (_, arguments) => Base64(arguments));
        handlebars.RegisterHelper("urlEncode", (_, arguments) => UrlEncode(arguments));
        handlebars.RegisterHelper("formatJson", (_, arguments) => FormatJson(Str(arguments, 0)));
        handlebars.RegisterHelper("formatXml", (_, arguments) => FormatXml(Str(arguments, 0)));
        handlebars.RegisterHelper("isOdd", (_, arguments) => Parity(arguments, wantOdd: true, "odd"));
        handlebars.RegisterHelper("isEven", (_, arguments) => Parity(arguments, wantOdd: false, "even"));
        handlebars.RegisterHelper("range", (_, arguments) => Range(arguments));
        handlebars.RegisterHelper("array", (_, arguments) => MakeArray(arguments));
        handlebars.RegisterHelper("lookup", (_, arguments) => LookupValue(arguments));
        handlebars.RegisterHelper("arrayAdd", (_, arguments) => ArrayAdd(arguments));
    }

    // {{arrayAdd coll item}} appends item to a live `array`/`range` list (or a `parseJson` array);
    // `position=N` inserts it at that index instead.
    private static object ArrayAdd(Arguments arguments)
    {
        var list = ToObjectList(arguments.Length > 0 ? arguments[0] : null);
        var item = arguments.Length > 1 ? arguments[1] : null;
        var position = HashInt(arguments, "position");
        if (position is { } index && index >= 0 && index <= list.Count)
        {
            list.Insert(index, item);
        }
        else
        {
            list.Add(item);
        }

        return list;
    }

    private static List<object?> ToObjectList(object? value) => value switch
    {
        IList<object?> list => [.. list],
        JArray array => [.. array.Select(token => (object?)ScalarOf(token))],
        _ => [],
    };

    private static int? HashInt(Arguments arguments, string key) =>
        int.TryParse(Hash(arguments, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    // {{lookup collection key}} — an element by index (list/array) or a value by key (map/object),
    // over a live `range`/`array` list or a `parseJson` token; a miss renders empty.
    private static object LookupValue(Arguments arguments)
    {
        var target = arguments.Length > 0 ? arguments[0] : null;
        var key = Str(arguments, 1);
        switch (target)
        {
            case JObject obj when obj.TryGetValue(key, out var value):
                return ScalarOf(value);
            case JArray array when int.TryParse(key, out var index) && index >= 0 && index < array.Count:
                return ScalarOf(array[index]);
            case IList<object?> list when int.TryParse(key, out var index) && index >= 0 && index < list.Count:
                return list[index] ?? string.Empty;
            case System.Collections.IDictionary map when map.Contains(key):
                return map[key] ?? string.Empty;
            default:
                return string.Empty;
        }
    }

    private static object ScalarOf(JToken token) =>
        token is JValue value ? value.Value ?? string.Empty : token.ToString();

    // {{range start end}} — an inclusive integer sequence (iterable with {{#each}}); verified by the differential suite.
    private static object Range(Arguments arguments)
    {
        var start = ToLong(Str(arguments, 0));
        var end = ToLong(Str(arguments, 1));
        var list = new List<object?>();
        for (var i = start; i <= end; i++)
        {
            list.Add(i);
        }

        return list;
    }

    // {{array a b c}} — an iterable of the positional arguments.
    private static object MakeArray(Arguments arguments)
    {
        var list = new List<object?>();
        for (var i = 0; i < arguments.Length; i++)
        {
            list.Add(arguments[i]);
        }

        return list;
    }

    private static long ToLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    // --- base64 / urlEncode / formatJson / formatXml (G2 helper long tail) --------------------

    // {{base64 value}} encodes UTF-8 → base64; decode=true reverses it; padding=false drops trailing '='.
    private static object Base64(Arguments arguments)
    {
        var value = Str(arguments, 0);
        if (Flag(arguments, "decode"))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return value;
            }
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return Hash(arguments, "padding") is "false" or "False" ? encoded.TrimEnd('=') : encoded;
    }

    // {{urlEncode value}} — form URL encoding (space → '+'), verified by the differential suite. decode=true reverses it.
    private static object UrlEncode(Arguments arguments)
    {
        var value = Str(arguments, 0);
        return Flag(arguments, "decode") ? WebUtility.UrlDecode(value) : WebUtility.UrlEncode(value);
    }

    // {{{formatJson json}}} pretty-prints (Jackson layout: `"k" : v`, `[ a, b ]`), reusing the shared writer.
    private static object FormatJson(string raw)
    {
        try
        {
            return JacksonJson.Write(JToken.Parse(raw));
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    // {{{formatXml xml}}} pretty-prints with a 2-space indent and a trailing newline; verified by the differential suite.
    private static object FormatXml(string raw)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(raw);
        }
        catch (System.Xml.XmlException)
        {
            return raw;
        }

        var builder = new StringBuilder();
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            OmitXmlDeclaration = true,
        };
        using (var writer = System.Xml.XmlWriter.Create(builder, settings))
        {
            document.Save(writer);
        }

        return builder.Append('\n').ToString();
    }

    // {{isOdd n}} / {{isEven n}} — jknack's CSS-class helpers: return "odd"/"even" (or an optional
    // override label) when the number's parity matches, else the empty string. Not block helpers.
    private static object Parity(Arguments arguments, bool wantOdd, string defaultLabel)
    {
        if (!long.TryParse(Str(arguments, 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ||
            (n % 2 != 0) != wantOdd)
        {
            return string.Empty;
        }

        return arguments.Length > 1 ? Str(arguments, 1) : defaultLabel;
    }

    private static bool Flag(Arguments arguments, string key) => Hash(arguments, key) is "true" or "True";

    private static string? Hash(Arguments arguments, string key) =>
        arguments.Hash is { } hash && hash.TryGetValue(key, out var value) ? value?.ToString() : null;

    // --- math ---------------------------------------------------------------------------------

    private static object Math(Arguments arguments)
    {
        var (left, leftInt) = Number(arguments, 0);
        var op = Str(arguments, 1);
        var (right, rightInt) = Number(arguments, 2);
        var bothInt = leftInt && rightInt;

        // Division and modulo by zero: the oracle answers 500. Rendering an empty value instead keeps a
        // template mistake from taking down the request path — a stated divergence, not an oversight.
        if (right == 0 && op is "/" or "%")
        {
            return string.Empty;
        }

        double result;
        switch (op)
        {
            case "+": result = left + right; break;
            case "-": result = left - right; break;
            case "*": result = left * right; break;
            case "/":
                // Integer division rounds the true quotient half AWAY FROM ZERO, which is what the
                // oracle does: -9/2 is -5, not -4. This used to be Floor(x + 0.5), which agrees for
                // positive operands and is wrong for every negative half — the case the differential
                // suite had no example of until one was added.
                result = bothInt
                    ? System.Math.Round(left / right, MidpointRounding.AwayFromZero)
                    : left / right;
                break;
            case "%":
                // Supported by the oracle, contrary to what this code used to claim. The sign follows
                // the dividend (-7 % 2 is -1), which is what both C# and Java do.
                result = left % right;
                break;
            default: return string.Empty; // '^' really is rejected by the oracle.
        }

        return bothInt
            ? ((long)result).ToString(CultureInfo.InvariantCulture)
            : JavaDouble(result);
    }

    // Java's Double.toString always shows a fractional part for an integral double (10.0, not 10).
    private static string JavaDouble(double value) =>
        double.IsFinite(value) && value == System.Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture) + ".0"
            : value.ToString("R", CultureInfo.InvariantCulture);

    // --- numberFormat -------------------------------------------------------------------------

    private static object NumberFormat(Arguments arguments)
    {
        if (!double.TryParse(Str(arguments, 0), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return string.Empty;
        }

        var format = Str(arguments, 1);
        return format switch
        {
            "currency" => value.ToString("C", EnUs),
            "percent" => ((long)System.Math.Round(value * 100, MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture) + "%",
            // A Java DecimalFormat pattern (`0.00`, `#,##0.0`, …) maps directly onto a .NET custom
            // numeric format string.
            _ => value.ToString(format, CultureInfo.InvariantCulture),
        };
    }

    // --- size / join --------------------------------------------------------------------------

    private static object Size(Arguments arguments)
    {
        // A live collection (from `range`/`array`/`parseJson`) is counted directly; otherwise fall back
        // to parsing the string form as JSON, else the string length.
        var value = arguments.Length > 0 ? arguments[0] : null;
        if (value is JArray liveArray)
        {
            return liveArray.Count;
        }

        if (value is System.Collections.ICollection collection && value is not string)
        {
            return collection.Count;
        }

        var raw = Str(arguments, 0);
        return TryParseJson(raw) switch
        {
            JArray array => array.Count,
            JObject obj => obj.Count,
            _ => raw.Length,
        };
    }

    private static object Join(Arguments arguments)
    {
        var separator = Str(arguments, 1);
        return TryParseJson(Str(arguments, 0)) is JArray array
            ? string.Join(separator, array.Select(RepresentItem))
            : Str(arguments, 0);
    }

    private static string RepresentItem(JToken token) => token.Type == JTokenType.String
        ? token.Value<string>() ?? string.Empty
        : token.ToString(Formatting.None);

    // --- substring / replace / capitalize -----------------------------------------------------

    private static object Substring(Arguments arguments)
    {
        var value = Str(arguments, 0);
        if (!int.TryParse(Str(arguments, 1), out var start) || start < 0 || start > value.Length)
        {
            return value;
        }

        if (arguments.Length > 2 && int.TryParse(Str(arguments, 2), out var end) && end >= start && end <= value.Length)
        {
            return value[start..end];
        }

        return value[start..];
    }

    private static object Replace(Arguments arguments) =>
        Str(arguments, 0).Replace(Str(arguments, 1), Str(arguments, 2));

    // Capitalizes the first letter of each whitespace-delimited word, leaving the rest unchanged
    // (jknack/WordUtils semantics).
    private static string Capitalize(string value)
    {
        var chars = value.ToCharArray();
        var atWordStart = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (atWordStart && char.IsLetter(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
            }

            atWordStart = char.IsWhiteSpace(chars[i]);
        }

        return new string(chars);
    }

    // --- shared -------------------------------------------------------------------------------

    private static (double Value, bool IsInteger) Number(Arguments arguments, int index)
    {
        var value = index < arguments.Length ? arguments[index] : null;
        switch (value)
        {
            case long l: return (l, true);
            case int i: return (i, true);
            case double d: return (d, false);
        }

        var text = value?.ToString() ?? string.Empty;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
        {
            return (parsedLong, true);
        }

        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedDouble)
            ? (parsedDouble, false)
            : (0, true);
    }

    private static JToken? TryParseJson(string value)
    {
        try
        {
            return JToken.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(Arguments arguments, int index) =>
        index < arguments.Length && arguments[index] is { } value ? value.ToString() ?? string.Empty : string.Empty;
}
