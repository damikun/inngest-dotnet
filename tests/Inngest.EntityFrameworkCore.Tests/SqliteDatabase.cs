using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore.Tests;

// An SQLite database for one test. Single is in memory and lives as long as the one connection to it that this object keeps
// open, which every context uses: the simplest setup as long as one thing at a time uses the database. Shared is a file in a
// temporary folder, in write-ahead mode, and every context opens a connection of its own to it, as against a real server.
// A file, because the in-memory database that connections share locks whole tables, and a dispatcher that runs while a test
// reads the table then fails now and then with "database table is locked", which a file waits out.
internal sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly string? _file;

    private SqliteDatabase(bool shared)
    {
        if (shared)
        {
            _file = Path.Combine(Path.GetTempPath(), $"inngest-outbox-{Guid.NewGuid():N}.db");
            using var connection = new SqliteConnection(ConnectionString(_file));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            command.ExecuteNonQuery();
        }
        else
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
        }
    }

    public static SqliteDatabase Single() => new(shared: false);

    public static SqliteDatabase Shared() => new(shared: true);

    public void Configure(DbContextOptionsBuilder options)
    {
        if (_file is not null)
        {
            options.UseSqlite(ConnectionString(_file));
        }
        else
        {
            options.UseSqlite(_connection!);
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        if (_file is not null)
        {
            foreach (var path in new[] { _file, _file + "-wal", _file + "-shm" })
            {
                File.Delete(path);
            }
        }
    }

    // No pool, so that closing a connection closes the file and the files can be deleted.
    private static string ConnectionString(string file) => new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ConnectionString;
}
