using System.Text.Json;
using Vaktr.Core.Interfaces;
using Vaktr.Core.Models;

namespace Vaktr.Store.Persistence;

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
    /// such as tests; normal app usage keeps the per-user Vaktr settings location.
    /// </summary>
    public JsonConfigStore(string? configPath = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath) ? null : Path.GetFullPath(configPath);
    }

    public async Task<VaktrConfig> LoadAsync(CancellationToken cancellationToken)
    {
        var path = _configPath ?? ResolveConfigPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return VaktrConfig.CreateDefault().Normalize();
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<VaktrConfig>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return (config ?? VaktrConfig.CreateDefault()).Normalize();
    }

    public async Task SaveAsync(VaktrConfig config, CancellationToken cancellationToken)
    {
        config = config.Normalize();
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var destination = _configPath ?? VaktrConfig.GetConfigPath();
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
        var currentPath = VaktrConfig.GetConfigPath();
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var legacyPath = VaktrConfig.GetLegacyConfigPath();
        return File.Exists(legacyPath) ? legacyPath : currentPath;
    }
}
