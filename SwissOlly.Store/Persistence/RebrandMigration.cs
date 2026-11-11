using SwissOlly.Core.Models;

namespace SwissOlly.Store.Persistence;

/// <summary>
/// Carries an install from before the rebrand (when the app was named Vaktr) over to the
/// SwissOlly locations: <c>%LocalAppData%\Vaktr</c> becomes <c>%LocalAppData%\SwissOlly</c>, and the
/// settings and database files take their new names. Runs first thing at launch, before anything
/// creates the new folder. Never throws: a failed step leaves the old files where they are, and
/// <see cref="SwissOllyConfig.Normalize"/> keeps pointing at the old storage folder until it moves.
/// </summary>
public static class RebrandMigration
{
    public static string Run()
    {
        try
        {
            var legacy = SwissOllyConfig.PreRebrandSettingsDirectory;
            var current = SwissOllyConfig.SettingsDirectory;
            var outcome = "nothing to migrate";

            if (Directory.Exists(legacy))
            {
                if (!Directory.Exists(current))
                {
                    // Same volume, so this is a single atomic rename of the whole tree.
                    Directory.Move(legacy, current);
                    outcome = "moved settings folder";
                }
                else
                {
                    // The new folder already exists (an earlier launch could not finish the move):
                    // bring across whatever is not there yet, one entry at a time.
                    foreach (var entry in Directory.EnumerateFileSystemEntries(legacy))
                    {
                        var target = Path.Combine(current, Path.GetFileName(entry));
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            continue;
                        }

                        if (Directory.Exists(entry))
                        {
                            Directory.Move(entry, target);
                        }
                        else
                        {
                            File.Move(entry, target);
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                    {
                        Directory.Delete(legacy);
                    }

                    outcome = "merged settings folder";
                }
            }

            RenameIfMissing(
                Path.Combine(current, SwissOllyConfig.PreRebrandSettingsFileName),
                Path.Combine(current, SwissOllyConfig.SettingsFileName));
            RenameIfMissing(
                Path.Combine(current, SwissOllyConfig.PreRebrandSettingsFileName + ".bak"),
                Path.Combine(current, SwissOllyConfig.SettingsFileName + ".bak"));
            MigrateDatabaseFiles(SwissOllyConfig.DefaultStorageDirectory);
            return outcome;
        }
        catch (Exception ex)
        {
            return $"incomplete: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Renames a pre-rebrand database (and its WAL/SHM companions) in <paramref name="directory"/>.
    /// Also called for custom storage folders, which the settings-folder move does not cover. The
    /// companions go first and every step is undone on failure: a database renamed without its
    /// WAL would silently lose the most recent samples.
    /// </summary>
    public static void MigrateDatabaseFiles(string directory)
    {
        try
        {
            var oldDb = Path.Combine(directory, SwissOllyConfig.PreRebrandDatabaseFileName);
            var newDb = Path.Combine(directory, SwissOllyConfig.DatabaseFileName);
            if (!File.Exists(oldDb) || File.Exists(newDb))
            {
                return;
            }

            var done = new List<(string From, string To)>();
            try
            {
                foreach (var suffix in new[] { "-wal", "-shm", string.Empty })
                {
                    var from = oldDb + suffix;
                    var to = newDb + suffix;
                    if (File.Exists(from))
                    {
                        File.Move(from, to);
                        done.Add((from, to));
                    }
                }
            }
            catch
            {
                for (var i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Move(done[i].To, done[i].From);
                    }
                    catch
                    {
                        // Best effort; the original is still intact under one of the two names.
                    }
                }
            }
        }
        catch
        {
            // Locked or unreadable: the store opens a new database and the old one stays put.
        }
    }

    private static void RenameIfMissing(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to))
            {
                File.Move(from, to);
            }
        }
        catch
        {
            // Best effort; JsonConfigStore falls back to defaults if settings are missing.
        }
    }
}
