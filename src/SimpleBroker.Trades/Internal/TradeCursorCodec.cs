namespace SimpleBroker.Trades.Internal;

using System.Globalization;
using System.Text;

internal readonly record struct TradeCursorValue(DateTime ExecutedAt, long Id);

internal static class TradeCursorCodec
{
    public static string Encode(DateTime executedAt, long id)
    {
        var payload = $"{executedAt.Ticks}:{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string cursor, out TradeCursorValue value)
    {
        value = default;
        if (cursor.Length is 0 or > 128 ||
            cursor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return false;

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = payload.IndexOf(':');
        if (separator <= 0 || separator == payload.Length - 1 ||
            !long.TryParse(payload.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < 0 || ticks > DateTime.MaxValue.Ticks ||
            !long.TryParse(payload.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            id <= 0)
            return false;

        value = new TradeCursorValue(new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}
