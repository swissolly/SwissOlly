using SwissOlly.Store.Persistence;

namespace SwissOlly.Tests;

public sealed class RebrandMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swissolly-rebrand-test-{Guid.NewGuid():N}");

    public RebrandMigrationTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void MigrateDatabaseFiles_Renames_Database_With_Its_Wal_And_Shm()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.WriteAllText(Path.Combine(_dir, SwissOllyConfig.PreRebrandDatabaseFileName + suffix), suffix);
        }

        RebrandMigration.MigrateDatabaseFiles(_dir);

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            Assert.False(File.Exists(Path.Combine(_dir, SwissOllyConfig.PreRebrandDatabaseFileName + suffix)));
            Assert.Equal(suffix, File.ReadAllText(Path.Combine(_dir, SwissOllyConfig.DatabaseFileName + suffix)));
        }
    }

    [Fact]
    public void MigrateDatabaseFiles_Never_Overwrites_An_Existing_Database()
    {
        File.WriteAllText(Path.Combine(_dir, SwissOllyConfig.PreRebrandDatabaseFileName), "old");
        File.WriteAllText(Path.Combine(_dir, SwissOllyConfig.DatabaseFileName), "new");

        RebrandMigration.MigrateDatabaseFiles(_dir);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_dir, SwissOllyConfig.DatabaseFileName)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dir, SwissOllyConfig.PreRebrandDatabaseFileName)));
    }

    [Fact]
    public async Task Store_Opens_A_PreRebrand_Database_In_A_Custom_Folder()
    {
        var config = new SwissOllyConfig { StorageDirectory = _dir }.Normalize();
        var store = new SqliteMetricStore();
        await store.InitializeAsync(config, CancellationToken.None);
        await store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Move(config.GetDatabasePath(), Path.Combine(_dir, SwissOllyConfig.PreRebrandDatabaseFileName));

        var reopened = new SqliteMetricStore();
        await reopened.InitializeAsync(config, CancellationToken.None);
        await reopened.DisposeAsync();

        Assert.True(File.Exists(config.GetDatabasePath()));
        Assert.False(File.Exists(Path.Combine(_dir, SwissOllyConfig.PreRebrandDatabaseFileName)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Temp folder; a locked leftover is harmless.
        }
    }
}
