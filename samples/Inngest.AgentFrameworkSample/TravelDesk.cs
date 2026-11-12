using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Inngest.AgentFrameworkSample;

// The agent's tools over a few made-up flights, with the bookings kept in memory.
public sealed class TravelDesk
{
    public const string Instructions = """
        You are a travel agent. Find flights with find_flights before you book one. Booking waits until a person approves
        it; when a person rejects a booking with a note, do what the note asks. Reply briefly.
        """;

    private static readonly Flight[] Flights =
    [
        new("OK 702", "Prague", "London", "07:10", 129),
        new("BA 855", "Prague", "London", "13:45", 164),
        new("OK 704", "Prague", "London", "18:30", 99),
        new("LH 1395", "Prague", "Frankfurt", "06:25", 142),
        new("LH 1399", "Prague", "Frankfurt", "16:50", 118),
    ];

    private readonly ConcurrentDictionary<string, Booking> _bookings = new();

    public TravelDesk() =>
        Tools =
        [
            AIFunctionFactory.Create(FindFlights, "find_flights"),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(BookFlight, "book_flight")),
        ];

    public IList<AITool> Tools { get; }

    public IReadOnlyCollection<Booking> Bookings => [.. _bookings.Values];

    [Description("Finds the flights between two cities on a date, with their prices in euros.")]
    private static Flight[] FindFlights(string from, string to, [Description("yyyy-MM-dd")] string date) =>
        [.. Flights.Where(flight => flight.From.Equals(from, StringComparison.OrdinalIgnoreCase) && flight.To.Equals(to, StringComparison.OrdinalIgnoreCase))];

    [Description("Books a seat on a flight for a passenger, and returns the booking.")]
    private Booking BookFlight(string flightNumber, string date, string passenger)
    {
        var flight = Flights.FirstOrDefault(flight => flight.Number.Equals(flightNumber, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There is no flight {flightNumber}.", nameof(flightNumber));
        var booking = new Booking($"TRV{_bookings.Count + 1:D4}", flight.Number, date, passenger, flight.PriceEur);
        _bookings[booking.Reference] = booking;
        return booking;
    }
}

public sealed record Flight(string Number, string From, string To, string Departs, int PriceEur);

public sealed record Booking(string Reference, string FlightNumber, string Date, string Passenger, int PriceEur);
