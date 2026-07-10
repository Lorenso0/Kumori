using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

/// <summary>
/// Opens connections to the tracking database with a busy timeout. Repositories
/// can use read-only connections; after the fixture parity gate the app uses a
/// read-write factory so the .NET tracker owns writes to the real DB.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _dbPath;
    private readonly bool _readOnly;

    public SqliteConnectionFactory(string dbPath, bool readOnly = true)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
    }

    public string DatabasePath => _dbPath;

    public bool DatabaseExists => File.Exists(_dbPath);

    public SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = _readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 5, // seconds; maps to busy_timeout behavior
        };
        var con = new SqliteConnection(builder.ConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return con;
    }
}
