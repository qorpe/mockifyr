using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HandlebarsDotNet;

namespace Mockifyr.Templating;

/// <summary>
/// Mockifyr's date/time Handlebars helpers (G2d): <c>parseDate</c> and <c>date</c>. All behaviors
/// are verified by the differential suite over <em>fixed</em> input instants — see
/// docs/parity/g2-response.md. <c>parseDate</c> turns a string into an instant (ISO-8601 by default,
/// or a Java <c>SimpleDateFormat</c> pattern via <c>format=</c>); <c>date</c> renders an instant,
/// applying an <c>offset=</c> and a Java format pattern (plus the special <c>epoch</c>/<c>unix</c>
/// tokens). The clock-dependent surface (<c>now</c>, unparseable-date fallback, and the
/// <c>timezone=</c> option, which the oracle ignores on a parsed instant) is deferred/racy.
/// </summary>
internal static class DateHelpers
{
    public static void Register(IHandlebars handlebars)
    {
        handlebars.RegisterHelper("parseDate", (_, arguments) => ParseDate(arguments));
        handlebars.RegisterHelper("date", (_, arguments) => FormatDate(arguments));
        handlebars.RegisterHelper("now", (_, arguments) => Now(arguments));
        handlebars.RegisterHelper("truncateDate", (_, arguments) => TruncateDate(arguments));
    }

    // {{truncateDate date '<unit>'}} floors an instant to the start of a calendar unit (using an
    // XMLUnit-style truncation vocabulary), returning the truncated instant for {{date}}.
    private static object TruncateDate(Arguments arguments)
    {
        if (arguments.Length == 0 || arguments[0] is not DateTimeOffset instant)
        {
            return RenderClock.UtcNow;
        }

        var offset = instant.Offset;
        return Str(arguments, 1) switch
        {
            "first second of minute" => new DateTimeOffset(instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, 0, offset),
            "first minute of hour" => new DateTimeOffset(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, offset),
            "first hour of day" => new DateTimeOffset(instant.Year, instant.Month, instant.Day, 0, 0, 0, offset),
            "first day of month" => new DateTimeOffset(instant.Year, instant.Month, 1, 0, 0, 0, offset),
            "first day of year" => new DateTimeOffset(instant.Year, 1, 1, 0, 0, 0, offset),
            _ => instant,
        };
    }

    // --- now ----------------------------------------------------------------------------------

    // The current instant, with the same offset= and format= surface as date (epoch/unix + Java
    // patterns). Its output is clock-dependent, so it can't be byte-diffed against the oracle; it is
    // validated structurally instead (correct format + a plausible-now value on both sides — see
    // docs/parity/g2-response.md). timezone= and truncate= are deferred.
    private static object Now(Arguments arguments)
    {
        var instant = RenderClock.UtcNow;

        var offset = Hash(arguments, "offset");
        if (offset is not null)
        {
            instant = ApplyOffset(instant, offset);
        }

        return Render(instant, Hash(arguments, "format"));
    }

    // --- parseDate ----------------------------------------------------------------------------

    private static object ParseDate(Arguments arguments)
    {
        var input = Str(arguments, 0);
        var format = Hash(arguments, "format");
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (format is not null)
        {
            if (DateTimeOffset.TryParseExact(
                    input, TranslateFormat(format), CultureInfo.InvariantCulture, styles, out var exact))
            {
                return exact;
            }
        }
        else if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, styles, out var parsed))
        {
            return parsed;
        }

        // Falls back to the current time on an unparseable date — racy, so never asserted.
        return RenderClock.UtcNow;
    }

    // --- date ---------------------------------------------------------------------------------

    private static object FormatDate(Arguments arguments)
    {
        // A non-date argument (e.g. a bare string) falls back to now — racy, not asserted.
        var instant = arguments.Length > 0 && arguments[0] is DateTimeOffset value ? value : RenderClock.UtcNow;

        var offset = Hash(arguments, "offset");
        if (offset is not null)
        {
            instant = ApplyOffset(instant, offset);
        }

        // The timezone= option is intentionally ignored: the oracle applies no shift to a parsed
        // instant (verified for Australia/Sydney and America/New_York). See the parity notes.
        return Render(instant, Hash(arguments, "format"));
    }

    private static readonly Regex OffsetPattern = new(@"^\s*(-?\d+)\s+([A-Za-z]+)\s*$", RegexOptions.Compiled);

    private static DateTimeOffset ApplyOffset(DateTimeOffset instant, string offset)
    {
        var match = OffsetPattern.Match(offset);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var amount))
        {
            return instant;
        }

        // Unit names are plural (a singular unit is rejected — verified by the differential suite).
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "seconds" => instant.AddSeconds(amount),
            "minutes" => instant.AddMinutes(amount),
            "hours" => instant.AddHours(amount),
            "days" => instant.AddDays(amount),
            "months" => instant.AddMonths(amount),
            "years" => instant.AddYears(amount),
            _ => instant,
        };
    }

    private static string Render(DateTimeOffset instant, string? format) => format switch
    {
        null => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        "epoch" => instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        "unix" => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        _ => instant.ToString(TranslateFormat(format), CultureInfo.InvariantCulture),
    };

    // --- Java SimpleDateFormat -> .NET custom format ------------------------------------------

    // Most letters are shared (y M d H h m s), so only the ones that differ are rewritten:
    // E -> day name, a -> AM/PM, S -> fractional second. Unsupported letters (zone, era, week…) are
    // emitted literally so the format never throws; those patterns are the deferred surface.
    private static string TranslateFormat(string javaPattern)
    {
        var sb = new StringBuilder(javaPattern.Length);
        var i = 0;
        while (i < javaPattern.Length)
        {
            var c = javaPattern[i];
            if (!char.IsLetter(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            var run = 1;
            while (i + run < javaPattern.Length && javaPattern[i + run] == c)
            {
                run++;
            }

            switch (c)
            {
                case 'E':
                    sb.Append(run >= 4 ? "dddd" : "ddd");
                    break;
                case 'a':
                    sb.Append("tt");
                    break;
                case 'S':
                    sb.Append('f', run);
                    break;
                case 'y' or 'M' or 'd' or 'H' or 'h' or 'm' or 's':
                    sb.Append(c, run);
                    break;
                default:
                    // Unsupported pattern letter: emit each occurrence as an escaped literal.
                    for (var n = 0; n < run; n++)
                    {
                        sb.Append('\\').Append(c);
                    }

                    break;
            }

            i += run;
        }

        return sb.ToString();
    }

    // --- shared -------------------------------------------------------------------------------

    private static string Str(Arguments arguments, int index) =>
        index < arguments.Length && arguments[index] is { } value ? value.ToString() ?? string.Empty : string.Empty;

    private static string? Hash(Arguments arguments, string key) =>
        arguments.Hash is { } hash && hash.TryGetValue(key, out var value) ? value?.ToString() : null;
}
