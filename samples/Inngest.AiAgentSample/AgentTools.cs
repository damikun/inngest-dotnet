using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Inngest.AiAgentSample;

public sealed class AgentTools(HttpClient http, IInngestRealtime realtime)
{
    public IList<AITool> For(RealtimeTopic<AgentUpdate> status) =>
    [
        AIFunctionFactory.Create(
            async ([Description("An IANA time zone, such as Europe/Bratislava")] string timeZone, CancellationToken cancellationToken) =>
            {
                await realtime.PublishAsync(status, new AgentUpdate("tool", $"Reading the clock in {timeZone}", "get_current_time"), cancellationToken);
                return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone)
                    ? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyy-MM-dd HH:mm (dddd)", CultureInfo.InvariantCulture)
                    : $"There is no time zone called {timeZone}.";
            },
            "get_current_time",
            "Gets the current date and time in a time zone."),

        AIFunctionFactory.Create(
            async ([Description("The name of a city")] string city, CancellationToken cancellationToken) =>
            {
                await realtime.PublishAsync(status, new AgentUpdate("tool", $"Looking up the weather in {city}", "get_weather"), cancellationToken);
                return await GetWeatherAsync(city, cancellationToken);
            },
            "get_weather",
            "Gets the current weather in a city."),
    ];

    private async Task<object> GetWeatherAsync(string city, CancellationToken cancellationToken)
    {
        var places = await http.GetFromJsonAsync<Places>(
            $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}", cancellationToken);
        if (places?.Results is not [var place, ..])
        {
            return new { error = $"No city called {city} was found." };
        }

        var forecast = await http.GetFromJsonAsync<Forecast>(string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude}&longitude={place.Longitude}&current=temperature_2m,wind_speed_10m"),
            cancellationToken);
        return new { place.Name, place.Country, temperatureCelsius = forecast?.Current.Temperature, windKmh = forecast?.Current.WindSpeed };
    }

    private sealed record Places(Place[]? Results);

    private sealed record Place(string Name, string? Country, double Latitude, double Longitude);

    private sealed record Forecast(Current Current);

    private sealed record Current(
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("wind_speed_10m")] double WindSpeed);
}
