using System.Text;
using System.Text.RegularExpressions;
using ZoaReference.Features.Nasr.Models;
using ZoaReference.Features.Nasr.Services;
using ZoaReference.Features.Terminal.Services;

namespace ZoaReference.Features.Terminal.Commands;

public class AirwayCommand(NasrDataService nasrDataService) : ITerminalCommand
{
    public string Name => "airway";
    public string[] Aliases => ["aw"];
    public string Summary => "Show fixes along an airway, or find airways containing a fix";
    public string Usage => "airway <id> [highlights...]\n" +
                           "    airway V25         — List all fixes along V25\n" +
                           "    airway V25 SJC MOD — Highlight SJC and MOD\n" +
                           "    airway SUNOL       — Find all airways containing SUNOL";

    private static readonly Regex AirwayPattern = new(@"^[VJQT]\d+[AH]?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<CommandResult> ExecuteAsync(CommandArgs args)
    {
        if (args.Positional.Length < 1)
            return CommandResult.FromError("Usage: airway <id> [highlights...]");

        var query = args.Positional[0].ToUpperInvariant();

        if (!AirwayPattern.IsMatch(query))
            return await ExecuteFixLookupAsync(query);

        var highlights = args.Positional.Length > 1
            ? args.Positional[1..].Select(h => h.ToUpperInvariant()).ToHashSet()
            : [];

        return await ExecuteAirwayLookupAsync(query, highlights);
    }

    // -------------------------------------------------------------------------
    // Airway lookup
    // -------------------------------------------------------------------------

    private async Task<CommandResult> ExecuteAirwayLookupAsync(string airwayId, HashSet<string> highlights)
    {
        var fixes = (await nasrDataService.GetAirwayFixes(airwayId))
            .OrderBy(f => f.Sequence)
            .ToList();

        if (fixes.Count == 0)
            return CommandResult.FromError($"No airway found with ID '{airwayId}'");

        var (direction, shouldReverse) = ComputeDirection(fixes);
        if (shouldReverse) fixes.Reverse();

        var dirStr = direction is not null ? $" ({direction})" : "";
        var sb = new StringBuilder();
        var widths = new[] { 6, 24, 26 };
        sb.Append(TextFormatter.FormatTableHeader(
            $"Airway {airwayId}{dirStr} — {fixes.Count} fixes",
            ["Seq", "Fix", "Coordinates"], widths));

        foreach (var fix in fixes)
        {
            var navaid = await nasrDataService.GetNavaidById(fix.FixId)
                         ?? await nasrDataService.GetNavaidByStationName(fix.FixId, fix.Latitude, fix.Longitude);
            // If resolved via station name, show short VOR ID (e.g. "EAT (WENATCHEE)" instead of "WENATCHEE")
            var fixLabel = navaid is not null
                ? (navaid.Id.Equals(fix.FixId, StringComparison.OrdinalIgnoreCase)
                    ? $"{fix.FixId} ({navaid.Name})"
                    : $"{navaid.Id} ({navaid.Name})")
                : fix.FixId;
            var isHighlighted = highlights.Contains(fix.FixId.ToUpperInvariant())
                                || (navaid is not null && highlights.Contains(navaid.Id.ToUpperInvariant()));

            string fixDisplay;
            int visibleLen;
            if (isHighlighted)
            {
                var bracketed = $"[{fixLabel}]";
                fixDisplay = TextFormatter.Colorize(bracketed, AnsiColor.Yellow);
                visibleLen = bracketed.Length;
            }
            else
            {
                fixDisplay = fixLabel;
                visibleLen = fixLabel.Length;
            }

            var coords = $"{fix.Latitude:F4}, {fix.Longitude:F4}";
            sb.Append("  ");
            sb.Append(fix.Sequence.ToString().PadRight(widths[0]));
            sb.Append(fixDisplay);
            sb.Append(new string(' ', Math.Max(0, widths[1] - visibleLen)));
            sb.AppendLine(coords);
        }

        var restrictions = await nasrDataService.GetAirwayRestrictions(airwayId);
        if (restrictions.Count > 0)
        {
            // Restrictions are keyed by the sequence of the fix ending the
            // segment; label each row with the adjacent display-order fixes.
            var bySeq = restrictions
                .GroupBy(r => r.Sequence)
                .ToDictionary(g => g.Key, g => g.First());
            var rows = new List<string[]>();
            for (var i = 1; i < fixes.Count; i++)
            {
                var from = fixes[i - 1];
                var to = fixes[i];
                if (!bySeq.TryGetValue(Math.Max(from.Sequence, to.Sequence), out var r)) continue;
                if (r.Mea is null && r.Moca is null) continue;
                rows.Add([
                    from.FixId, to.FixId,
                    r.Mea.HasValue ? $"{r.Mea:N0}" : "-",
                    r.Moca.HasValue ? $"{r.Moca:N0}" : "-"
                ]);
            }

            if (rows.Count > 0)
            {
                sb.AppendLine();
                var rWidths = new[] { 12, 12, 10, 10 };
                sb.Append(TextFormatter.FormatTableHeader($"MEA/MOCA — {airwayId}",
                    ["From", "To", "MEA", "MOCA"], rWidths));
                foreach (var row in rows)
                {
                    sb.AppendLine(TextFormatter.FormatTableRow(row, rWidths));
                }
            }
        }

        return CommandResult.FromText(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Reverse fix lookup
    // -------------------------------------------------------------------------

    private async Task<CommandResult> ExecuteFixLookupAsync(string fixId)
    {
        var airwayIds = await nasrDataService.FindAirwaysContainingFix(fixId);

        // NASR AWY stores navaid station names (e.g. "MISSION BAY" → "MISSION"), not short IDs.
        // If no direct match, check if fixId is a navaid and search by the first word of its name.
        var effectiveFixId = fixId;
        if (airwayIds.Count == 0)
        {
            var navaid = await nasrDataService.GetNavaidById(fixId);
            if (navaid is not null)
            {
                var firstWord = navaid.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstWord is not null)
                {
                    var byName = await nasrDataService.FindAirwaysContainingFix(firstWord);
                    if (byName.Count > 0)
                    {
                        airwayIds = byName;
                        effectiveFixId = firstWord;
                    }
                }
            }
        }

        if (airwayIds.Count == 0)
            return CommandResult.FromError($"No airways found containing '{fixId}'");

        var sorted = airwayIds.OrderBy(AirwaySortKey).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(TextFormatter.Colorize($"FIX {fixId} — found on {sorted.Count} airway(s)", AnsiColor.Orange));
        sb.AppendLine();

        foreach (var airwayId in sorted)
        {
            var fixes = (await nasrDataService.GetAirwayFixes(airwayId))
                .OrderBy(f => f.Sequence)
                .ToList();

            var (direction, shouldReverse) = ComputeDirection(fixes);
            if (shouldReverse) fixes.Reverse();

            var dirStr = direction is not null ? $" ({direction})" : "";
            var names = fixes.Select(f => f.FixId.ToUpperInvariant()).ToList();
            var idx = names.IndexOf(effectiveFixId.ToUpperInvariant());
            if (idx < 0) continue;

            var start = Math.Max(0, idx - 2);
            var end = Math.Min(names.Count, idx + 3);
            var snippetParts = new List<string>();

            for (var i = start; i < end; i++)
            {
                var fix = fixes[i];
                var navaid = await nasrDataService.GetNavaidById(fix.FixId)
                             ?? await nasrDataService.GetNavaidByStationName(fix.FixId, fix.Latitude, fix.Longitude);
                var resolvedId = navaid is not null && !navaid.Id.Equals(fix.FixId, StringComparison.OrdinalIgnoreCase)
                    ? navaid.Id : fix.FixId;
                var label = navaid is not null ? $"{resolvedId} ({navaid.Name})" : fix.FixId;

                if (fix.FixId.Equals(effectiveFixId, StringComparison.OrdinalIgnoreCase))
                {
                    // Show original query ID (e.g. "MZB") if it differs from stored name
                    var displayId = effectiveFixId.Equals(fixId, StringComparison.OrdinalIgnoreCase)
                        ? resolvedId
                        : fixId;
                    var navaidForDisplay = navaid ?? await nasrDataService.GetNavaidById(fixId);
                    var displayLabel = navaidForDisplay is not null
                        ? $"{displayId} ({navaidForDisplay.Name})"
                        : displayId;
                    label = TextFormatter.Colorize($"[{displayLabel}]", AnsiColor.Yellow);
                }

                snippetParts.Add(label);
            }

            var prefix = start > 0 ? ".." : "";
            var suffix = end < names.Count ? ".." : "";
            var snippet = prefix + string.Join("..", snippetParts) + suffix;

            sb.AppendLine($"  {airwayId,-6}{dirStr,-14} {fixes.Count,2} fixes   {snippet}");
        }

        return CommandResult.FromText(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (string? direction, bool shouldReverse) ComputeDirection(IReadOnlyList<AirwayFix> fixes)
    {
        var withCoords = fixes.Where(f => f.Latitude != 0 || f.Longitude != 0).ToList();
        if (withCoords.Count < 2) return (null, false);

        var first = withCoords[0];
        var last = withCoords[^1];
        var latDiff = last.Latitude - first.Latitude;
        var lonDiff = last.Longitude - first.Longitude;

        static string Cardinal(double lat, double lon)
        {
            const double t = 0.5;
            var ns = Math.Abs(lat) > t ? (lat > 0 ? "N" : "S") : "";
            var ew = Math.Abs(lon) > t ? (lon > 0 ? "E" : "W") : "";
            if (ns == "" && ew == "")
                return Math.Abs(lat) >= Math.Abs(lon) ? (lat > 0 ? "N" : "S") : (lon > 0 ? "E" : "W");
            return ns + ew;
        }

        bool rev;
        if (Math.Abs(lonDiff) > 0.5) rev = lonDiff < 0;       // going west → reverse
        else if (Math.Abs(latDiff) > 0.5) rev = latDiff > 0;  // going north → reverse
        else rev = false;

        var (effLat, effLon) = rev ? (-latDiff, -lonDiff) : (latDiff, lonDiff);
        var dir = $"{Cardinal(-effLat, -effLon)} to {Cardinal(effLat, effLon)}";
        return (dir, rev);
    }

    private static string AirwaySortKey(string id)
    {
        // Sort V first, then J, T, Q, others; numeric within prefix
        var p = id.Length > 0 ? id[0] switch { 'V' => '0', 'J' => '1', 'T' => '2', 'Q' => '3', _ => '9' } : '9';
        var num = id.Length > 1 && int.TryParse(id[1..], out var n) ? n : 0;
        return $"{p}{num:D6}";
    }

    public IEnumerable<string> GetCompletions(string partial, int argIndex) => [];
}
