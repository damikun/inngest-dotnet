using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Outbox;

// The outbox against the dev server: an event saved with the app's data starts the function that listens for it, and a
// send that is repeated under the same id starts nothing. The second is what makes it safe for a dispatcher to send
// an event again when it cannot tell whether the first send was taken.
public sealed class OutboxTests(EvaluationEnvironment environment) : EvaluationScenario<OutboxFixture>(environment)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact(Timeout = 90_000)]
    public async Task An_event_saved_through_the_outbox_starts_its_function_and_a_resend_under_the_same_id_starts_nothing()
    {
        var message = await SaveOrderAsync("A-1");
        await Observed.WaitForAsync("placed", Wait, Cancellation);
        Observed.Values<string>("placed").ShouldBe(["A-1"]);

        // A dispatcher that died between sending and marking leaves the row as it was before the send. Put it back, and the
        // dispatcher sends the event again, under the same id.
        await ResetAsync(message.Id);
        var resent = await Poll.UntilAsync(
            "the dispatcher to send the event again",
            _ => ReadAsync(message.Id),
            row => row.Attempts == 2 && row.SentAt != null,
            Wait,
            Cancellation);
        resent.LastError.ShouldBeNull();

        // Inngest took the second request and started no run for it.
        await Task.Delay(NoRunWindow + NoRunWindow, Cancellation);
        Observed.Values<string>("placed").ShouldBe(["A-1"]);

        // The same data under another id is another event, which does start a run: the id is what was recognized.
        await SaveOrderAsync("A-1");
        await Poll.UntilAsync("the second event to start its run", _ => Observed.Values<string>("placed"), placed => placed.Count == 2, Wait, Cancellation);
    }

    [Fact(Timeout = 90_000)]
    public async Task An_event_in_a_rolled_back_transaction_starts_nothing()
    {
        using (var scope = Fixture.App.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ShopContext>();
            await using var transaction = await database.Database.BeginTransactionAsync(Cancellation);
            database.Orders.Add(new ShopOrder { Number = "B-1" });
            database.AddInngestEvent(new OrderPlaced(TestId, "B-1"));
            await database.SaveChangesAsync(Cancellation);
            await transaction.RollbackAsync(Cancellation);
        }

        // A committed event after it shows that the dispatcher was running, and that only this one went out.
        await SaveOrderAsync("B-2");
        await Observed.WaitForAsync("placed", Wait, Cancellation);
        await Task.Delay(NoRunWindow, Cancellation);

        Observed.Values<string>("placed").ShouldBe(["B-2"]);
    }

    private async Task<InngestOutboxMessage> SaveOrderAsync(string number)
    {
        using var scope = Fixture.App.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ShopContext>();
        database.Orders.Add(new ShopOrder { Number = number });
        var message = database.AddInngestEvent(new OrderPlaced(TestId, number));
        await database.SaveChangesAsync(Cancellation);
        return message;
    }

    private async Task ResetAsync(string id)
    {
        using var scope = Fixture.App.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ShopContext>();
        await database.Set<InngestOutboxMessage>()
            .Where(message => message.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(message => message.SentAt, (DateTime?)null), Cancellation);
    }

    private InngestOutboxMessage ReadAsync(string id)
    {
        using var scope = Fixture.App.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ShopContext>();
        return database.Set<InngestOutboxMessage>().AsNoTracking().Single(message => message.Id == id);
    }
}
