using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Outbox;

// A dev server and an app that keeps orders and the events about them in one database and sends the events with the
// outbox. The database is an SQLite file in write-ahead mode, which the app's connections share, so that this fixture needs
// no container beside the server's; the outbox on a real server is tested against PostgreSQL in PostgresOutboxTests. A file,
// because the in-memory database that connections share locks whole tables, and the dispatcher reads and updates it while the
// tests do.
public sealed class OutboxFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        var file = Path.Combine(Path.GetTempPath(), $"inngest-outbox-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            command.ExecuteNonQuery();
        }

        using (var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(connectionString).Options))
        {
            context.Database.EnsureCreated();
        }

        // The fixture cannot clean up after itself, so the files go when the test run ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var path in new[] { file, file + "-wal", file + "-shm" })
            {
                File.Delete(path);
            }
        };

        // Polling every second is what sends a row again after the test has reset it; a save wakes the dispatcher at once.
        inngest.AddEntityFrameworkOutbox<ShopContext>(options => options.PollInterval = TimeSpan.FromSeconds(1));
        inngest.Services.AddDbContext<ShopContext>(options => options.UseSqlite(connectionString).UseInngestOutbox());

        inngest.AddFunction<OrderPlaced>(
            new FunctionOptions { Id = "outbox-order-placed", Triggers = [Trigger.Event<OrderPlaced>()] },
            static (context, _) =>
            {
                context.Observed().Record("placed", context.Event.Data.OrderNumber);
                return Task.FromResult<object?>(null);
            });
    }
}
