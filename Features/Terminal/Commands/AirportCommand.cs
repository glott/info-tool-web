using System.Text;
using ZoaReference.Features.IcaoReference.Repositories;
using ZoaReference.Features.Terminal.Services;

namespace ZoaReference.Features.Terminal.Commands;

public class AirportCommand(AirportRepository airportRepository) : ITerminalCommand
{
    public string Name => "airport";
    public string[] Aliases => ["ap"];
    public string Summary => "Search airport by ICAO/IATA code or name";
    public string Usage => "airport <query>\n" +
                           "    airport KSFO   — Exact ICAO match\n" +
                           "    airport SFO    — IATA or local ID match\n" +
                           "    airport san fr — Name search";

    public Task<CommandResult> ExecuteAsync(CommandArgs args)
    {
        if (args.Positional.Length < 1)
        {
            return Task.FromResult(CommandResult.FromError("Usage: airport <query>"));
        }

        var query = string.Join(" ", args.Positional);
        var queryUpper = query.ToUpperInvariant();

        // Try exact ICAO match first
        if (airportRepository.TryGetValue(queryUpper, out var exact) && exact is not null)
        {
            return Task.FromResult(FormatAirports([exact], query));
        }

        // Try K-prefix
        if (query.Length == 3 && airportRepository.TryGetValue($"K{queryUpper}", out var kExact) && kExact is not null)
        {
            return Task.FromResult(FormatAirports([kExact], query));
        }

        // Search by IATA, local ID, or name — code matches sort first so a
        // valid code is never crowded out by name-substring hits.
        bool MatchCode(IcaoReference.Models.Airport a) =>
            a.IataId.Equals(queryUpper, StringComparison.OrdinalIgnoreCase) ||
            a.LocalId.Equals(queryUpper, StringComparison.OrdinalIgnoreCase);

        var results = airportRepository.AllAirports
            .Where(a => MatchCode(a) || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(MatchCode)
            .ThenBy(a => a.Name)
            .Take(25)
            .ToList();

        if (results.Count == 0)
        {
            return Task.FromResult(CommandResult.FromError($"No airports found matching '{query}'"));
        }

        return Task.FromResult(FormatAirports(results, query));
    }

    private static CommandResult FormatAirports(List<IcaoReference.Models.Airport> airports, string query)
    {
        var sb = new StringBuilder();
        var widths = new[] { 8, 8, 36, 8 };
        sb.Append(TextFormatter.FormatTableHeader($"Airports — '{query}'", ["ICAO", "IATA", "Name", "FIR"], widths));

        foreach (var ap in airports)
        {
            sb.AppendLine(TextFormatter.FormatTableRow(
                [ap.IcaoId, ap.IataId, ap.Name, ap.Fir],
                widths));
        }

        return CommandResult.FromText(sb.ToString());
    }

    public IEnumerable<string> GetCompletions(string partial, int argIndex)
    {
        if (argIndex <= 1)
        {
            return airportRepository.AllAirportIds
                .Where(id => id.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Take(20);
        }
        return [];
    }
}
