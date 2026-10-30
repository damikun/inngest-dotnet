using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Outbox;

// A dev server and an app that keeps orders and the events about them in one database and sends the events with the
// outbox. The database is SQLite in memory, shared by the app's connections, which keeps this fixture to one container;
// the outbox on a real server is tested against PostgreSQL in PostgresOutboxTests.
public sealed class OutboxFixture : EvaluationFixture
{
    // Held open for as long as the test run lives, which keeps the in-memory database alive.
    private SqliteConnection? _keepAlive;

    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        var connectionString = $"DataSource=file:outbox-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        using (var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(connectionString).Options))
        {
            context.Database.EnsureCreated();
        }

        // Polling every second is what sends a row again after the test has reset it; a save wakes the dispatcher at once.
        inngest.AddEntityFrameworkOutbox<ShopContext>(options => options.PollInterval = TimeSpan.FromSeconds(1));
        inngest.Services.AddDbContext<ShopContext>((services, options) => options.UseSqlite(connectionString).UseInngestOutbox(services));

        inngest.AddFunction<OrderPlaced>(
            new FunctionOptions { Id = "outbox-order-placed", Triggers = [Trigger.Event<OrderPlaced>()] },
            static (context, _) =>
            {
                context.Observed().Record("placed", context.Event.Data.OrderNumber);
                return Task.FromResult<object?>(null);
            });
    }
}
