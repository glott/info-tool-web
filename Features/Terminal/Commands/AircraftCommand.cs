using System.Text;
using ZoaReference.Features.IcaoReference.Repositories;
using ZoaReference.Features.Terminal.Services;

namespace ZoaReference.Features.Terminal.Commands;

public class AircraftCommand(AircraftTypeRepository aircraftTypeRepository) : ITerminalCommand
{
    public string Name => "aircraft";
    public string[] Aliases => ["ac"];
    public string Summary => "Search aircraft by ICAO type code or manufacturer/model";
    public string Usage => "aircraft <query>\n" +
                           "    aircraft B738     — Search by ICAO type code\n" +
                           "    aircraft boeing   — Search by manufacturer/model";

    public Task<CommandResult> ExecuteAsync(CommandArgs args)
    {
        if (args.Positional.Length < 1)
        {
            return Task.FromResult(CommandResult.FromError("Usage: aircraft <query>"));
        }

        var query = string.Join(" ", args.Positional);

        // Mirror the /codes page: type designators match exactly; only fall
        // back to manufacturer/model search when no type matches. Every query
        // term must appear in "Manufacturer Model" so "boeing 737" works even
        // though the terms span both fields.
        var results = aircraftTypeRepository.AllAircraftTypes
            .Where(a => a.IcaoId.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (results.Count == 0)
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            results = aircraftTypeRepository.AllAircraftTypes
                .Where(a =>
                {
                    var combined = $"{a.Manufacturer} {a.Model}";
                    return terms.All(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }

        results = results.Take(25).ToList();

        if (results.Count == 0)
        {
            return Task.FromResult(CommandResult.FromError($"No aircraft types found matching '{query}'"));
        }

        var sb = new StringBuilder();
        var widths = new[] { 8, 30, 6, 4, 5, 5, 6 };
        sb.Append(TextFormatter.FormatTableHeader($"Aircraft Types — '{query}'",
            ["Type", "Manufacturer/Model", "Eng", "Wt", "CWT", "SRS", "LAHSO"], widths));

        foreach (var ac in results)
        {
            var mfrModel = $"{ac.Manufacturer} {ac.Model}";
            sb.AppendLine(TextFormatter.FormatTableRow(
                [ac.IcaoId, Truncate(mfrModel, 28), ac.FaaEngineNumberType, ac.FaaWeightClass,
                 ac.ConsolidatedWakeTurbulenceCategory, ac.SameRunwaySeparationCategory,
                 ac.LandAndHoldShortGroup],
                widths));
        }

        return Task.FromResult(CommandResult.FromText(sb.ToString()));
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";

    public IEnumerable<string> GetCompletions(string partial, int argIndex)
    {
        if (argIndex <= 1)
        {
            return aircraftTypeRepository.AllAircraftTypes
                .Select(a => a.IcaoId)
                .Distinct()
                .Where(id => id.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Take(20);
        }
        return [];
    }
}
