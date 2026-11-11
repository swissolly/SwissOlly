using System.Text.Json;
using SwissOlly.Core.Interfaces;
using SwissOlly.Core.Models;

namespace SwissOlly.Store.Persistence;

public sealed class JsonConfigStore : IConfigStore
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _configPath;

    /// <summary>
    /// Creates a config store. Supplying a path is useful for isolated environments
    /// such as tests; normal app usage keeps the per-user SwissOlly settings location.
    /// </summary>
    public JsonConfigStore(string? configPath = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath) ? null : Path.GetFullPath(configPath);
    }

    public async Task<SwissOllyConfig> LoadAsync(CancellationToken cancellationToken)
    {
        var path = _configPath ?? ResolveConfigPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return SwissOllyConfig.CreateDefault().Normalize();
        }

        SwissOllyConfig? config;
        try
        {
            await using var stream = File.OpenRead(path);
            config = await JsonSerializer.DeserializeAsync<SwissOllyConfig>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Truncated by a crash mid-write on an older build, or hand-edited badly. Starting on
            // defaults beats failing startup; the original is kept beside it for recovery.
            PreserveCorruptConfig(path);
            config = null;
        }

        return (config ?? SwissOllyConfig.CreateDefault()).Normalize();
    }

    private static void PreserveCorruptConfig(string path)
    {
        try
        {
            File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch
        {
            // Best effort; the next save replaces the corrupt file either way.
        }
    }

    public async Task SaveAsync(SwissOllyConfig config, CancellationToken cancellationToken)
    {
        config = config.Normalize();
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = _configPath ?? SwissOllyConfig.GetConfigPath();
            var destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            Directory.CreateDirectory(config.StorageDirectory);

            var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // A leftover temporary file is harmless; preserve the original save error.
                }
            }
        }
        finally
        {
            SaveGate.Release();
        }
    }

    private static string ResolveConfigPath()
    {
        var currentPath = SwissOllyConfig.GetConfigPath();
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var legacyPath = SwissOllyConfig.GetLegacyConfigPath();
        return File.Exists(legacyPath) ? legacyPath : currentPath;
    }
}
