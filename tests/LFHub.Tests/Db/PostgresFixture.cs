using System.Security.Cryptography;
using LFHub.Db;
using Npgsql;

namespace LFHub.Tests.Db;

/// <summary>
/// A fresh, migrated database on the Postgres server named by LFHUB_TEST_DATABASE (a
/// connection string whose user may create databases), dropped afterwards. Without the
/// variable the tests that need it are skipped, except under CI (CI=true), where they fail.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Variable = "LFHUB_TEST_DATABASE";

    private readonly string? _server = Environment.GetEnvironmentVariable(Variable);
    private readonly string _database = $"lfhub_test_{Guid.NewGuid():N}";
    private NpgsqlDataSource? _dataSource;
    private readonly List<(string Name, NpgsqlDataSource DataSource)> _empty = [];

    public SecretBox Box { get; } = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(_server))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException($"{Variable} must be set under CI");
            return;
        }
        await using (var admin = NpgsqlDataSource.Create(_server))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {_database}"))
            await cmd.ExecuteNonQueryAsync();
        _dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_server) { Database = _database }.ConnectionString);
        new HubStore(_dataSource, Box).Migrate();
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is null) return;
        await _dataSource.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(_server!);
        foreach (var (name, dataSource) in _empty)
        {
            await dataSource.DisposeAsync();
            await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
        await using var cmd = admin.CreateCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
        await cmd.ExecuteNonQueryAsync();
    }

    /// Another database on the same server, not migrated, dropped with this one.
    public async Task<NpgsqlDataSource> EmptyDatabaseAsync()
    {
        _ = DataSource;
        var name = $"lfhub_test_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(_server!))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();
        var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_server) { Database = name }.ConnectionString);
        _empty.Add((name, dataSource));
        return dataSource;
    }

    /// The data source, or a skipped test when no server is configured.
    public NpgsqlDataSource DataSource
    {
        get
        {
            Assert.SkipWhen(_dataSource is null, $"{Variable} is not set");
            return _dataSource!;
        }
    }

    public HubStore Store => new(DataSource, Box);

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? default : (T)result;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
