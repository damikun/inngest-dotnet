using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore.Tests;

// An SQLite database in memory that exists as long as one connection to it is open, which this object keeps open. Shared
// means every context opens a connection of its own to it, as against a real server; otherwise they all use the one
// connection, which is the simplest setup as long as one thing at a time uses the database.
internal sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _shared;

    private SqliteDatabase(bool shared)
    {
        _shared = shared;
        _connection = new SqliteConnection(shared
            ? $"DataSource=file:outbox-{Guid.NewGuid():N}?mode=memory&cache=shared"
            : "DataSource=:memory:");
        _connection.Open();
    }

    public static SqliteDatabase Single() => new(shared: false);

    public static SqliteDatabase Shared() => new(shared: true);

    public void Configure(DbContextOptionsBuilder options)
    {
        if (_shared)
        {
            options.UseSqlite(_connection.ConnectionString);
        }
        else
        {
            options.UseSqlite(_connection);
        }
    }

    public void Dispose() => _connection.Dispose();
}
