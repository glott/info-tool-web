using System.Text.RegularExpressions;
using ZoaReference.Features.Nasr.Models;

namespace ZoaReference.Features.Nasr.Services;

public static partial class NasrParser
{
    /// <summary>
    /// Parses NAV.txt fixed-width records for navaids (VOR, VORTAC, TACAN, NDB, etc.).
    /// NASR NAV1 records: positions based on FAA NASR layout specification.
    /// </summary>
    public static List<NavaidInfo> ParseNavaids(string text)
    {
        var navaids = new List<NavaidInfo>();
        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 100) continue;

            var recordType = SafeSubstring(line, 0, 4).Trim();
            if (recordType != "NAV1") continue;

            var type = SafeSubstring(line, 8, 20).Trim();
            var id = SafeSubstring(line, 28, 4).Trim();
            var name = SafeSubstring(line, 42, 30).Trim();
            var city = SafeSubstring(line, 72, 40).Trim();
            var state = SafeSubstring(line, 142, 2).Trim();

            // Latitude: formatted seconds at position 371, length 14
            var latStr = SafeSubstring(line, 371, 14).Trim();
            var lonStr = SafeSubstring(line, 396, 14).Trim();
            var lat = ParseNasrCoordinate(latStr);
            var lon = ParseNasrCoordinate(lonStr);

            var frequency = SafeSubstring(line, 533, 6).Trim();
            var variation = SafeSubstring(line, 413, 5).Trim();

            if (string.IsNullOrEmpty(id)) continue;

            navaids.Add(new NavaidInfo(id, name, type, frequency, lat, lon, variation, city, state));
        }

        return navaids;
    }

    /// <summary>
    /// Parses AWY.txt fixed-width records for airways and their fixes.
    /// NASR AWY2 records contain airway fix information.
    /// </summary>
    public static List<AirwayFix> ParseAirwayFixes(string text)
    {
        var fixes = new List<AirwayFix>();
        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 100) continue;

            var recordType = SafeSubstring(line, 0, 4).Trim();
            if (recordType != "AWY2") continue;

            var airwayId = ParseAirwayKey(line);
            var seqStr = SafeSubstring(line, 10, 5).Trim();

            // Prefer the short identifier marker in the record tail
            // (e.g. "SJC V334 *SJC*D" → "SJC", "V334 *SUNOL*CA" → "SUNOL");
            // fall back to the first word of the station name field.
            var fixId = FixIdMarkerRegex().Match(SafeSubstring(line, 111, line.Length)) is { Success: true } m
                ? m.Groups[1].Value
                : SafeSubstring(line, 15, 30).Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            var latStr = SafeSubstring(line, 83, 14).Trim();
            var lonStr = SafeSubstring(line, 97, 14).Trim();
            var lat = ParseNasrCoordinate(latStr);
            var lon = ParseNasrCoordinate(lonStr);

            if (!int.TryParse(seqStr, out var sequence)) sequence = 0;

            if (string.IsNullOrEmpty(fixId) || string.IsNullOrEmpty(airwayId)) continue;

            fixes.Add(new AirwayFix(fixId, airwayId, sequence, lat, lon));
        }

        return fixes;
    }

    /// <summary>
    /// Parses AWY.txt fixed-width records for airway MEA/MOCA restrictions.
    /// NASR AWY1 records carry the altitude data, keyed by the sequence of the
    /// fix that ends the segment: MEA at 74-79, MEA opposite direction at
    /// 85-90, MOCA at 101-106. All values are feet.
    /// </summary>
    public static List<AirwayRestriction> ParseAirwayRestrictions(string text)
    {
        var restrictions = new List<AirwayRestriction>();
        using var reader = new StringReader(text);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 110) continue;

            var recordType = SafeSubstring(line, 0, 4).Trim();
            if (recordType != "AWY1") continue;

            var airwayId = ParseAirwayKey(line);
            if (string.IsNullOrEmpty(airwayId)) continue;

            var seqStr = SafeSubstring(line, 10, 5).Trim();
            if (!int.TryParse(seqStr, out var sequence)) continue;

            var mea = ParseAltitudeFeet(line, 74);
            var meaOpposite = ParseAltitudeFeet(line, 85);
            var moca = ParseAltitudeFeet(line, 101);
            if (mea is null && meaOpposite is null && moca is null) continue;

            restrictions.Add(new AirwayRestriction(airwayId, sequence, mea, meaOpposite, moca));
        }

        return restrictions;
    }

    /// <summary>
    /// Extracts the airway key from an AWY record header. NASR reuses airway
    /// designators across regions (e.g. V334 exists in both California and
    /// Alaska); the one-char airway type at position 9 (blank = CONUS,
    /// "A" = Alaska, "H" = Hawaii) is appended to keep them distinct,
    /// e.g. "V334" vs "V334A".
    /// </summary>
    private static string ParseAirwayKey(string line) =>
        SafeSubstring(line, 4, 5).Trim() + SafeSubstring(line, 9, 1).Trim();

    private static int? ParseAltitudeFeet(string line, int start)
    {
        var s = SafeSubstring(line, start, 5).Trim();
        return s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, out var v) ? v : null;
    }

    [GeneratedRegex(@"\*([A-Z]{2,5})\*")]
    private static partial Regex FixIdMarkerRegex();

    /// <summary>
    /// Parses NASR-formatted coordinates like "37-37-08.070N" or "122-23-14.630W"
    /// Also handles decimal seconds format: "373708.070N"
    /// </summary>
    public static double ParseNasrCoordinate(string coord)
    {
        if (string.IsNullOrWhiteSpace(coord)) return 0.0;

        coord = coord.Trim();
        var hemisphere = coord[^1];
        var numPart = coord[..^1];

        double degrees, minutes, seconds;

        if (numPart.Contains('-'))
        {
            // Format: DD-MM-SS.SSS
            var parts = numPart.Split('-');
            if (parts.Length < 3) return 0.0;
            double.TryParse(parts[0], out degrees);
            double.TryParse(parts[1], out minutes);
            double.TryParse(parts[2], out seconds);
        }
        else
        {
            // Format: DDMMSS.SSS or DDDMMSS.SSS
            var dotIdx = numPart.IndexOf('.');
            var intPart = dotIdx >= 0 ? numPart[..dotIdx] : numPart;
            var fracPart = dotIdx >= 0 ? numPart[dotIdx..] : "";

            if (intPart.Length >= 6)
            {
                var degLen = intPart.Length - 4;
                double.TryParse(intPart[..degLen], out degrees);
                double.TryParse(intPart[degLen..(degLen + 2)], out minutes);
                double.TryParse(intPart[(degLen + 2)..] + fracPart, out seconds);
            }
            else
            {
                return 0.0;
            }
        }

        var result = degrees + (minutes / 60.0) + (seconds / 3600.0);
        if (hemisphere is 'S' or 'W') result = -result;
        return result;
    }

    private static string SafeSubstring(string s, int start, int length)
    {
        if (start >= s.Length) return "";
        var end = Math.Min(start + length, s.Length);
        return s[start..end];
    }
}
