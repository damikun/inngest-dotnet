using Npgsql;
using Testcontainers.PostgreSql;

namespace Inngest.IntegrationTests.Outbox;

// A PostgreSQL server in Docker for the tests of a class. Every test gets a database of its own on it, so tests run in
// parallel without seeing each other's rows.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithLabel(Harness.InngestServer.Label, "true")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    // Creates an empty database and returns the connection string for it.
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"outbox_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
