using LFHub.Db;

namespace LFHub.Tests.Db;

/// Migrations against data written under an earlier schema.
public class MigrationTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OnePatientPerHub_KeepsTheOldestAndMovesTheOthersDevicesToIt()
    {
        var dataSource = await db.EmptyDatabaseAsync();
        var store = new HubStore(dataSource, db.Box);
        store.Migrate(5);
        await using (var insert = dataSource.CreateCommand("""
            INSERT INTO lfhub.patients (id, name, created_at) VALUES
                ('00000000-0000-0000-0000-000000000002', 'Second', '2026-02-01Z'),
                ('00000000-0000-0000-0000-000000000001', 'First', '2026-01-01Z'),
                ('00000000-0000-0000-0000-000000000003', 'Third', '2026-03-01Z');
            INSERT INTO lfhub.devices (patient_id, name, role, key_hash) VALUES
                ('00000000-0000-0000-0000-000000000001', 'a', 'admin', 'h1'),
                ('00000000-0000-0000-0000-000000000002', 'b', 'admin', 'h2'),
                ('00000000-0000-0000-0000-000000000003', 'c', 'admin', 'h3');
            """))
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        store.Migrate();

        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal(first, (await store.PatientAsync())?.Id);
        await using (var count = dataSource.CreateCommand("SELECT count(*) FROM lfhub.patients"))
            Assert.Equal(1L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, (await store.DevicesForPatientAsync(first)).Count);
        await using (var version = dataSource.CreateCommand("SELECT max(version) FROM lfhub.schema_version"))
            Assert.Equal(HubStore.SchemaVersion, await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.CreatePatientAsync("Another"));
    }

    [Fact]
    public async Task OnePatientPerHub_MigratesAnEmptyDatabase()
    {
        var store = new HubStore(await db.EmptyDatabaseAsync(), db.Box);
        store.Migrate();
        Assert.Null(await store.PatientAsync());
        await store.CreatePatientAsync("Only");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.CreatePatientAsync("Another"));
    }

    [Fact]
    public async Task Migrate_FromSeveralHubsAtOnceRunsEachMigrationOnce()
    {
        var dataSource = await db.EmptyDatabaseAsync();
        var stores = Enumerable.Range(0, 4).Select(_ => new HubStore(dataSource, db.Box)).ToList();
        await Task.WhenAll(stores.Select(store => Task.Run(store.Migrate, TestContext.Current.CancellationToken)));
        await using var count = dataSource.CreateCommand("SELECT count(*) FROM lfhub.schema_version");
        Assert.Equal((long)HubStore.SchemaVersion, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
