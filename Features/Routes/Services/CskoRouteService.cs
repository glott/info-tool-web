using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ZoaReference.Features.Routes.Models;

namespace ZoaReference.Features.Routes.Services;

public class CskoRouteService(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<AppSettings> appSettings,
    IMemoryCache cache,
    ILogger<CskoRouteService> logger)
{
    /// <summary>
    /// Fetches real-world routes filed between <paramref name="lookback"/> ago and now.
    /// Callers must specify the window, e.g. TimeSpan.FromDays(183) for ~6 months.
    /// </summary>
    public async Task<AirportPairRouteSummary> FetchRoutesAsync(string departureIcao, string arrivalIcao,
        TimeSpan lookback)
    {
        if (cache.TryGetValue<AirportPairRouteSummary>(MakeCacheKey(departureIcao, arrivalIcao, lookback), out var routeSummary))
        {
            return routeSummary!;
        }

        var url = MakeUrl(departureIcao, arrivalIcao, lookback);
        try
        {
            var client = httpClientFactory.CreateClient();

            // Fetch manually (rather than GetFromJsonAsync) so a non-success
            // response's body can be logged -- APIs like this one usually explain
            // *why* a request was rejected (e.g. expected format for "since")
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                logger.LogError("Route Service returned {status} for {url}: {body}",
                    (int)response.StatusCode, url, errorBody);
                response.EnsureSuccessStatusCode(); // throws HttpRequestException
            }

            var routes = await response.Content.ReadFromJsonAsync<FlightRoutesRoot>();
            var returnRouteSummary = new AirportPairRouteSummary(departureIcao, arrivalIcao);
            var routesDict = new Dictionary<string, FlightRouteSummary>();

            if (routes?.Routes is not null)
            {
                foreach (var fetchedSummary in routes.Routes)
                {
                    var newRoute = new FlightRouteSummary
                    {
                        RouteFrequency = fetchedSummary.Count,
                        DepartureIcaoId = fetchedSummary.Departure,
                        ArrivalIcaoId = fetchedSummary.Arrival,
                        MinAltitude = fetchedSummary.MinAltitude,
                        MaxAltitude = fetchedSummary.MaxAltitude,
                        Route = fetchedSummary.RouteText,
                        DistanceMi = null,
                        Flights = new List<RealWorldFlight>()
                    };
                    returnRouteSummary.FlightRouteSummaries.Add(newRoute);
                    routesDict[fetchedSummary.RouteText] = newRoute;
                }
            }

            if (routes?.MostRecent is not null)
            {
                foreach (var flight in routes.MostRecent)
                {
                    var newFlight = new RealWorldFlight
                    {
                        DepartureIcaoId = flight.Departure,
                        ArrivalIcaoId = flight.Arrival,
                        Callsign = flight.AircraftId,
                        AircraftIcaoId = flight.AircraftType,
                        Altitude = flight.AssignedAltitude,
                        Route = flight.RouteText,
                        Distance = null
                    };

                    if (routesDict.TryGetValue(flight.RouteText, out var route))
                    {
                        route.Flights.Add(newFlight);
                    }

                    returnRouteSummary.MostRecent.Add(newFlight);
                }
            }

            // Cache result before returning. Cache the mapped summary (not the raw
            // FlightRoutesRoot): the lookup above reads AirportPairRouteSummary, and
            // a type mismatch would make TryGetValue miss on every request.
            var expiration = DateTimeOffset.UtcNow.AddSeconds(appSettings.CurrentValue.CacheTtls.FlightAwareRoutes);
            cache.Set(MakeCacheKey(departureIcao, arrivalIcao, lookback), returnRouteSummary, expiration);

            return returnRouteSummary;
        }
        catch (HttpRequestException e)
        {
            logger.LogError("Error fetching Route Service url {url}: {error}", url, e);
            throw e;
        }
    }

    // Note: the original URL was missing the '=' after "since" ("&since<timestamp>"),
    // so the API ignored the malformed parameter and returned all-time data.
    private string MakeUrl(string departureIcao, string arrivalIcao, TimeSpan lookback) =>
        appSettings.CurrentValue.Urls.CskoRouteBase +
        "departure=" + departureIcao + "&arrival=" + arrivalIcao +
        "&since=" + SinceTimestamp(lookback);

    // Cache key includes the lookback so results for different windows don't collide
    private static (string, string, double) MakeCacheKey(string departureIcao, string arrivalIcao, TimeSpan lookback) => (
        $"CskoDeparture:{departureIcao.ToUpper()}", $"CskoArrival:{arrivalIcao.ToUpper()}", lookback.TotalDays);

    // Unix epoch SECONDS. Date strings ("2026-02-24") are rejected with 400, and
    // epoch milliseconds likely overflow a 32-bit parse; seconds fit. If the API
    // still rejects this, the logged response body in FetchRoutesAsync will show
    // the format it expects.
    private static string SinceTimestamp(TimeSpan lookback) =>
        DateTimeOffset.UtcNow.Subtract(lookback).ToUnixTimeSeconds().ToString();
}

public class FlightRoutesRoot
{
    [JsonPropertyName("routes")] public List<Route> Routes { get; set; }

    [JsonPropertyName("adapted_routes")] public List<AdaptedRoute> AdaptedRoutes { get; set; }

    [JsonPropertyName("most_recent")] public List<MostRecent> MostRecent { get; set; }
}

public class AdaptedRoute
{
    [JsonPropertyName("departure")] public string Departure { get; set; }

    [JsonPropertyName("arrival")] public string Arrival { get; set; }

    [JsonPropertyName("route_type")] public string RouteType { get; set; }

    [JsonPropertyName("route_id")] public string RouteId { get; set; }

    [JsonPropertyName("route")] public string Route { get; set; }
}

public class MostRecent
{
    [JsonPropertyName("aircraft_id")] public string AircraftId { get; set; }

    [JsonPropertyName("departure")] public string Departure { get; set; }

    [JsonPropertyName("arrival")] public string Arrival { get; set; }

    [JsonPropertyName("assigned_altitude")]
    public int? AssignedAltitude { get; set; }

    [JsonPropertyName("route_text")] public string RouteText { get; set; }

    [JsonPropertyName("aircraft_type")] public string AircraftType { get; set; }

    [JsonPropertyName("equipment_qualifier")]
    public string EquipmentQualifier { get; set; }

    [JsonPropertyName("timestamp")] public object Timestamp { get; set; }
}

public class Route
{
    [JsonPropertyName("count")] public int Count { get; set; }

    [JsonPropertyName("departure")] public string Departure { get; set; }

    [JsonPropertyName("arrival")] public string Arrival { get; set; }

    [JsonPropertyName("route_text")] public string RouteText { get; set; }

    [JsonPropertyName("min_altitude")] public int? MinAltitude { get; set; }

    [JsonPropertyName("max_altitude")] public int? MaxAltitude { get; set; }

    [JsonPropertyName("equipment_qualifiers")]
    public string EquipmentQualifiers { get; set; }

    [JsonPropertyName("aircraft_types")] public string AircraftTypes { get; set; }
}