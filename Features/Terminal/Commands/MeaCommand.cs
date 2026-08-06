using System.Text;
using System.Text.RegularExpressions;
using ZoaReference.Features.Nasr.Models;
using ZoaReference.Features.Nasr.Services;
using ZoaReference.Features.Terminal.Services;

namespace ZoaReference.Features.Terminal.Commands;

public partial class MeaCommand(NasrDataService nasrDataService) : ITerminalCommand
{
    private sealed record MeaSegment(string Airway, string FromFix, string ToFix, int MeaFt, int? MocaFt);

    private sealed record RouteLeg(string Airway, string EntryFix, string ExitFix);

    public string Name => "mea";
    public string[] Aliases => [];
    public string Summary => "Show MEA/MOCA for an airway route or between two fixes";
    public string Usage => "mea <route...> [-a altitude]\n" +
                           "    mea SAC V25 SWR               — Show MEA for V25 segment SAC → SWR\n" +
                           "    mea KSFO V25 SAC J80 KRNO     — Multi-airway route\n" +
                           "    mea SAC V25 SWR -a 70         — Check if 7,000 ft clears the MEA\n" +
                           "    mea SJC MOD                   — Fallback: common airways between two fixes";

    public async Task<CommandResult> ExecuteAsync(CommandArgs args)
    {
        if (args.Positional.Length < 2)
        {
            return CommandResult.FromError("Usage: mea <route...> [-a altitude]");
        }

        // Accept -a, --a, or --altitude for the alert altitude in hundreds of feet.
        int? alertAltitude = null;
        foreach (var key in new[] { "a", "altitude" })
        {
            if (args.Flags.TryGetValue(key, out var altStr) && altStr is not null && int.TryParse(altStr, out var alt))
            {
                alertAltitude = alt;
                break;
            }
        }
        var alertAltFt = alertAltitude.HasValue ? alertAltitude.Value * 100 : (int?)null;

        // Dot-separators are common in filed routes ("SAC.V25.SWR"); normalize
        // them to spaces so the user can paste a filed route verbatim.
        var tokens = args.Positional
            .SelectMany(p => p.Split('.', StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.ToUpperInvariant())
            .ToArray();

        // Route mode vs fix-pair fallback: if the user gave us any airway
        // tokens, treat the whole thing as a route; otherwise fall back to
        // "common airways between two fixes" for the existing two-arg shortcut.
        var hasAirway = tokens.Any(IsAirway);

        if (hasAirway)
        {
            return await RunRouteMode(tokens, alertAltFt);
        }

        if (tokens.Length != 2)
        {
            return CommandResult.FromError(
                "Without an airway in the route, mea expects exactly two fixes.\n" +
                "Usage: mea <route...> [-a altitude]");
        }

        return await RunFixPairMode(tokens[0], tokens[1], alertAltFt);
    }

    private async Task<CommandResult> RunRouteMode(string[] tokens, int? alertAltFt)
    {
        var legs = ParseRoute(tokens);
        if (legs.Count == 0)
        {
            return CommandResult.FromError(
                "Could not parse any airway segments from the route.\n" +
                "Each airway needs a fix before and after it, e.g. `SAC V25 SWR`.");
        }

        var allSegments = new List<MeaSegment>();
        var unresolvedLegs = new List<string>();

        foreach (var leg in legs)
        {
            var segs = await CollectSegmentsForLeg(leg);
            if (segs.Count == 0)
            {
                unresolvedLegs.Add($"{leg.EntryFix} {leg.Airway} {leg.ExitFix}");
                continue;
            }
            allSegments.AddRange(segs);
        }

        if (allSegments.Count == 0)
        {
            var detail = unresolvedLegs.Count > 0
                ? "\nNo MEA/MOCA data for: " + string.Join(", ", unresolvedLegs)
                : "";
            return CommandResult.FromError("No MEA/MOCA data found for this route." + detail);
        }

        var routeLabel = string.Join(" ", tokens);
        return RenderSegments(routeLabel, allSegments, alertAltFt, unresolvedLegs);
    }

    private async Task<CommandResult> RunFixPairMode(string fix1, string fix2, int? alertAltFt)
    {
        var fix1Coords = await nasrDataService.GetWaypointCoordinates(fix1);
        var fix2Coords = await nasrDataService.GetWaypointCoordinates(fix2);

        if (fix1Coords is null)
        {
            return CommandResult.FromError($"Fix '{fix1}' not found in NASR data");
        }
        if (fix2Coords is null)
        {
            return CommandResult.FromError($"Fix '{fix2}' not found in NASR data");
        }

        var fix1Airways = await nasrDataService.FindAirwaysContainingFix(fix1);
        var fix2Airways = await nasrDataService.FindAirwaysContainingFix(fix2);
        var fix2Set = new HashSet<string>(fix2Airways, StringComparer.OrdinalIgnoreCase);
        var commonAirways = fix1Airways.Where(a => fix2Set.Contains(a)).ToList();

        if (commonAirways.Count == 0)
        {
            return CommandResult.FromError($"No common airway found between {fix1} and {fix2}");
        }

        // For each common airway, run the same segment-range collection used
        // by route mode so fix-pair mode shares one code path.
        var allSegments = new List<MeaSegment>();
        foreach (var airwayId in commonAirways)
        {
            var leg = new RouteLeg(airwayId, fix1, fix2);
            allSegments.AddRange(await CollectSegmentsForLeg(leg));
        }

        if (allSegments.Count == 0)
        {
            return CommandResult.FromError($"No MEA/MOCA data found between {fix1} and {fix2}");
        }

        return RenderSegments($"{fix1} → {fix2}", allSegments, alertAltFt, unresolvedLegs: []);
    }

    /// <summary>
    /// Walks a route token stream and produces one <see cref="RouteLeg"/> per
    /// airway token, scanning backward/forward for the nearest fix-like token.
    /// Mirrors the scan logic in <c>mea.py:get_mea_for_route</c>.
    /// </summary>
    private static List<RouteLeg> ParseRoute(string[] tokens)
    {
        var legs = new List<RouteLeg>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!IsAirway(tokens[i]))
            {
                continue;
            }

            var airway = tokens[i];

            string? entryFix = null;
            for (var j = i - 1; j >= 0; j--)
            {
                if (IsFixToken(tokens[j]))
                {
                    entryFix = tokens[j];
                    break;
                }
            }

            string? exitFix = null;
            for (var j = i + 1; j < tokens.Length; j++)
            {
                if (IsFixToken(tokens[j]))
                {
                    exitFix = tokens[j];
                    break;
                }
            }

            if (entryFix is not null && exitFix is not null)
            {
                legs.Add(new RouteLeg(airway, entryFix, exitFix));
            }
        }
        return legs;
    }

    /// <summary>
    /// For a single leg (airway + entry/exit fixes), finds all restrictions
    /// whose endpoints fall within the traversed sequence range on the airway.
    /// Mirrors <c>mea.py:get_mea_for_route</c>: when one of the entry/exit fixes
    /// can't be resolved on the airway (e.g. the user gave us an airport ICAO
    /// at the route boundary), fall back to returning every restriction on the
    /// airway rather than silently dropping the leg.
    /// </summary>
    private async Task<List<MeaSegment>> CollectSegmentsForLeg(RouteLeg leg)
    {
        var fixes = await nasrDataService.GetAirwayFixes(leg.Airway);
        var restrictions = await nasrDataService.GetAirwayRestrictions(leg.Airway);
        if (fixes.Count == 0 || restrictions.Count == 0)
        {
            return [];
        }

        int? entrySeq = null, exitSeq = null;
        foreach (var fix in fixes)
        {
            if (entrySeq is null && fix.FixId.Equals(leg.EntryFix, StringComparison.OrdinalIgnoreCase))
            {
                entrySeq = fix.Sequence;
            }
            if (exitSeq is null && fix.FixId.Equals(leg.ExitFix, StringComparison.OrdinalIgnoreCase))
            {
                exitSeq = fix.Sequence;
            }
        }

        var orderedFixes = fixes.OrderBy(f => f.Sequence).ToList();
        var segments = new List<MeaSegment>();
        foreach (var r in restrictions)
        {
            if (r.Mea is null) continue;

            // A restriction at sequence N covers the segment ending at the fix
            // with sequence N. When both route endpoints resolved, keep only
            // segments inside the traversed range (excluding the segment that
            // ends at the entry fix itself).
            if (entrySeq is not null && exitSeq is not null)
            {
                var minSeq = Math.Min(entrySeq.Value, exitSeq.Value);
                var maxSeq = Math.Max(entrySeq.Value, exitSeq.Value);
                if (r.Sequence <= minSeq || r.Sequence > maxSeq) continue;
            }

            var endIdx = orderedFixes.FindIndex(f => f.Sequence == r.Sequence);
            if (endIdx < 0) continue;
            var toFix = orderedFixes[endIdx].FixId;
            var fromFix = endIdx > 0 ? orderedFixes[endIdx - 1].FixId : $"seq{r.Sequence - 10}";

            segments.Add(new MeaSegment(
                leg.Airway,
                fromFix,
                toFix,
                r.Mea.Value,
                r.Moca));
        }
        return segments;
    }

    private static CommandResult RenderSegments(
        string routeLabel,
        List<MeaSegment> allSegments,
        int? alertAltFt,
        List<string> unresolvedLegs)
    {
        allSegments.Sort((a, b) => b.MeaFt.CompareTo(a.MeaFt));
        var maxMea = allSegments[0].MeaFt;

        var sb = new StringBuilder();

        if (alertAltFt.HasValue)
        {
            if (alertAltFt.Value >= maxMea)
            {
                sb.AppendLine(TextFormatter.Colorize(
                    $"SAFE: {alertAltFt.Value:N0} ft meets MEA requirement of {maxMea:N0} ft on {routeLabel}",
                    AnsiColor.Green));
            }
            else
            {
                sb.AppendLine(TextFormatter.Colorize(
                    $"WARNING: {alertAltFt.Value:N0} ft is BELOW required MEA of {maxMea:N0} ft on {routeLabel}",
                    AnsiColor.Yellow));
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"Maximum MEA on {routeLabel}: {maxMea:N0} ft");
            sb.AppendLine();
        }

        // Group segments by airway, preserving the route order (first
        // appearance wins). Within each airway group, keep the sequence as
        // returned by CollectSegmentsForLeg.
        var airwayOrder = new List<string>();
        var byAirway = new Dictionary<string, List<MeaSegment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in allSegments.OrderByDescending(s => s.MeaFt))
        {
            if (!byAirway.TryGetValue(seg.Airway, out var list))
            {
                list = [];
                byAirway[seg.Airway] = list;
                airwayOrder.Add(seg.Airway);
            }
            list.Add(seg);
        }

        foreach (var airwayId in airwayOrder)
        {
            var airwaySegs = byAirway[airwayId];
            var widths = new[] { 12, 12, 10, 10 };
            sb.Append(TextFormatter.FormatTableHeader($"MEA/MOCA — {airwayId}",
                ["From", "To", "MEA", "MOCA"], widths));

            foreach (var seg in airwaySegs)
            {
                var meaStr = $"{seg.MeaFt:N0}";
                var mocaStr = seg.MocaFt.HasValue ? $"{seg.MocaFt.Value:N0}" : "-";

                if (alertAltFt.HasValue && seg.MeaFt > alertAltFt.Value)
                {
                    meaStr = TextFormatter.Colorize(meaStr, AnsiColor.Red);
                }

                sb.AppendLine(TextFormatter.FormatTableRow([seg.FromFix, seg.ToFix, meaStr, mocaStr], widths));
            }
        }

        if (unresolvedLegs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(TextFormatter.Colorize(
                "  No MEA/MOCA data for: " + string.Join(", ", unresolvedLegs),
                AnsiColor.Gray));
        }

        return CommandResult.FromText(sb.ToString());
    }

    private static bool IsAirway(string token) => AirwayRegex().IsMatch(token);

    /// <summary>
    /// SID/STAR identifiers look like <c>CNDEL5</c> or <c>SCOLA1A</c> — alpha
    /// then digit then optional alpha — and are always longer than 5 chars
    /// (plain fixes like <c>SAC</c> or <c>MOD</c> don't match). When scanning
    /// for a fix adjacent to an airway we skip these so the entry/exit come
    /// from the fix on either side of the procedure, not the procedure itself.
    /// </summary>
    private static bool IsSidStarName(string token) =>
        token.Length > 5 && SidStarRegex().IsMatch(token);

    private static bool IsFixToken(string token)
    {
        if (token.Equals("DCT", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsAirway(token)) return false;
        if (IsSidStarName(token)) return false;
        return true;
    }

    [GeneratedRegex(@"^[VJTQ]\d+$")]
    private static partial Regex AirwayRegex();

    [GeneratedRegex(@"^[A-Z]+\d+[A-Z]*$")]
    private static partial Regex SidStarRegex();

    public IEnumerable<string> GetCompletions(string partial, int argIndex) => [];
}
