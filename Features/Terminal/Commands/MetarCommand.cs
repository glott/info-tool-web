using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoaReference.Features.Terminal.Services;

namespace ZoaReference.Features.Terminal.Commands;

public class MetarCommand(IHttpClientFactory httpClientFactory) : ITerminalCommand
{
    private const string MetarApiUrl = "https://aviationweather.gov/api/data/metar";
    private const int MaxStations = 20;
    private const int MaxStationIdLength = 5;

    public string Name => "metar";
    public string[] Aliases => [];
    public string Summary => "Fetch current METAR weather observation";
    public string Usage => "metar <station...>\n" +
                           "    metar SFO          — METAR for San Francisco\n" +
                           "    metar KSFO         — Same (ICAO format)\n" +
                           "    metar SFO OAK RNO  — Multiple stations at once";

    public async Task<CommandResult> ExecuteAsync(CommandArgs args)
    {
        if (args.Positional.Length < 1)
        {
            return CommandResult.FromError("Usage: metar <station...>");
        }

        if (args.Positional.Length > MaxStations)
        {
            return CommandResult.FromError($"Too many stations (max {MaxStations}).");
        }

        var stations = args.Positional
            .Select(NormalizeStation)
            .ToList();

        var invalidStation = stations.FirstOrDefault(s => s.Length > MaxStationIdLength || !s.All(char.IsLetterOrDigit));
        if (invalidStation is not null)
        {
            return CommandResult.FromError($"Invalid station identifier: '{invalidStation}'");
        }

        List<MetarObservation> observations;
        try
        {
            observations = await FetchMetars(stations);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            return CommandResult.FromError($"Failed to fetch METAR: {ex.Message}");
        }

        if (observations.Count == 0)
        {
            return CommandResult.FromError($"No METAR data found for: {string.Join(", ", stations)}");
        }

        var sb = new StringBuilder();
        foreach (var obs in observations)
        {
            FormatMetar(sb, obs);
        }

        return CommandResult.FromText(sb.ToString());
    }

    private void FormatMetar(StringBuilder sb, MetarObservation obs)
    {
        var cat = obs.FlightCategory ?? "UNK";
        var catColor = cat switch
        {
            "VFR" => AnsiColor.Green,
            "MVFR" => AnsiColor.Cyan,
            "IFR" => AnsiColor.Red,
            "LIFR" => AnsiColor.Magenta,
            _ => AnsiColor.White
        };

        // Header
        var header = $"METAR — {obs.IcaoId}";
        if (!string.IsNullOrEmpty(obs.Name))
        {
            header += $" ({obs.Name})";
        }
        sb.AppendLine(TextFormatter.Colorize($"  {new string('═', 76)}", AnsiColor.Orange));
        sb.AppendLine(TextFormatter.Colorize($"  {header}", AnsiColor.Orange));
        sb.AppendLine(TextFormatter.Colorize($"  {new string('═', 76)}", AnsiColor.Orange));

        // Raw METAR
        sb.AppendLine($"  {obs.RawOb}");
        sb.AppendLine();

        // Flight category
        sb.AppendLine($"  Flight Category: {TextFormatter.Colorize(cat, catColor)}");

        // Wind
        var wind = FormatWind(obs);
        sb.AppendLine($"  Wind:            {TextFormatter.Colorize(wind, AnsiColor.Cyan)}");

        // Altimeter — API returns value in mb, convert to inHg (1 mb = 0.02953 inHg)
        if (obs.Altimeter.HasValue)
        {
            var inHg = obs.Altimeter.Value * 0.02953;
            var altStr = $"A{inHg:F2}";
            sb.AppendLine($"  Altimeter:       {TextFormatter.Colorize(altStr, AnsiColor.Cyan)}");
        }

        // Visibility
        if (!string.IsNullOrEmpty(obs.Visibility))
        {
            sb.AppendLine($"  Visibility:      {obs.Visibility} SM");
        }

        // Clouds
        if (obs.Clouds is { Count: > 0 })
        {
            string? lowestLayer = null;
            string? ceiling = null;

            foreach (var cloud in obs.Clouds)
            {
                if (string.IsNullOrEmpty(cloud.Cover) || !cloud.Base.HasValue)
                {
                    continue;
                }
                lowestLayer ??= $"{cloud.Cover} {cloud.Base.Value:N0}ft AGL";
                if (ceiling is null && cloud.Cover is "BKN" or "OVC" or "VV")
                {
                    ceiling = $"{cloud.Cover} {cloud.Base.Value:N0}ft AGL";
                }
            }

            if (lowestLayer is not null)
            {
                sb.AppendLine($"  Lowest Layer:    {lowestLayer}");
            }
            sb.AppendLine(ceiling is not null
                ? $"  Ceiling:         {ceiling}"
                : "  Ceiling:         Unlimited");
        }

        // Temperature / Dewpoint
        if (obs.Temperature.HasValue)
        {
            var dew = obs.Dewpoint.HasValue ? $" / Dewpoint: {obs.Dewpoint.Value:F1}C" : "";
            sb.AppendLine($"  Temperature:     {obs.Temperature.Value:F1}C{dew}");
        }

        // Weather phenomena
        if (!string.IsNullOrEmpty(obs.WxString))
        {
            sb.AppendLine($"  Weather:         {obs.WxString}");
        }

        sb.AppendLine();
    }

    private static string FormatWind(MetarObservation obs)
    {
        if (string.IsNullOrEmpty(obs.WindDirection) || obs.WindSpeed is null || obs.WindSpeed == 0)
        {
            return "Calm";
        }
        var gust = obs.WindGust.HasValue ? $"G{obs.WindGust.Value}" : "";
        // wdir is a number normally, "VRB" for variable winds
        var dir = int.TryParse(obs.WindDirection, out var deg) ? $"{deg:D3}" : obs.WindDirection;
        return $"{dir}{obs.WindSpeed:D2}{gust}KT";
    }

    private static string NormalizeStation(string station)
    {
        station = station.ToUpperInvariant().Trim();
        if (station.Length == 3)
        {
            station = "K" + station;
        }
        return station;
    }

    private async Task<List<MetarObservation>> FetchMetars(List<string> stations)
    {
        var ids = string.Join(",", stations);
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("zoa-reference-web/1.0");
        var url = $"{MetarApiUrl}?ids={ids}&format=json";
        var result = await client.GetFromJsonAsync<List<MetarObservation>>(url);
        return result ?? [];
    }

    public IEnumerable<string> GetCompletions(string partial, int argIndex) => [];

    private sealed class MetarObservation
    {
        [JsonPropertyName("icaoId")]
        public string? IcaoId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("rawOb")]
        public string? RawOb { get; set; }

        [JsonPropertyName("temp")]
        public double? Temperature { get; set; }

        [JsonPropertyName("dewp")]
        public double? Dewpoint { get; set; }

        [JsonPropertyName("wdir")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string? WindDirection { get; set; }

        [JsonPropertyName("wspd")]
        public int? WindSpeed { get; set; }

        [JsonPropertyName("wgst")]
        public int? WindGust { get; set; }

        [JsonPropertyName("visib")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string? Visibility { get; set; }

        [JsonPropertyName("altim")]
        public double? Altimeter { get; set; }

        [JsonPropertyName("fltCat")]
        public string? FlightCategory { get; set; }

        [JsonPropertyName("clouds")]
        public List<CloudLayer>? Clouds { get; set; }

        [JsonPropertyName("wxString")]
        public string? WxString { get; set; }
    }

    private sealed class CloudLayer
    {
        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("base")]
        public int? Base { get; set; }
    }

    // aviationweather.gov mixes types on some fields: wdir is a number or "VRB",
    // visib is a number or a string like "10+"
    private sealed class StringOrNumberConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Unexpected token {reader.TokenType} for string-or-number field")
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }
}
