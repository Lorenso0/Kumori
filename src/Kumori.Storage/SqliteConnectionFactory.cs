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
    private readonly string _connectionString;

    public SqliteConnectionFactory(string dbPath, bool readOnly = true)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = _readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 5,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath => _dbPath;

    public bool DatabaseExists => File.Exists(_dbPath);

    public SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }
}
