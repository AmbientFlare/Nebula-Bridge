using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public sealed record IndexerDefinitionSource(
    string Path,
    string Yaml,
    string? Error = null,
    int DefinitionVersion = IndexerDefinition.SupportedSchemaVersion
);

public interface IIndexerDefinitionProvider
{
    string Name { get; }

    string DefinitionsDirectory { get; }

    Task<IReadOnlyList<IndexerDefinitionSource>> LoadAsync(
        CancellationToken cancellationToken
    );
}

public sealed class LocalIndexerDefinitionProvider : IIndexerDefinitionProvider
{
    private const int MaximumDefinitions = 1000;
    private const int MaximumDefinitionBytes = 512 * 1024;
    private readonly ILogger<LocalIndexerDefinitionProvider> _logger;

    public LocalIndexerDefinitionProvider(
        IApplicationPaths applicationPaths,
        ILogger<LocalIndexerDefinitionProvider> logger
    )
        : this(
            Environment.GetEnvironmentVariable("NEBULA_BRIDGE_INDEXER_DEFINITIONS")
                ?? Path.Combine(applicationPaths.DataPath, "nebulabridge", "indexers"),
            logger
        )
    { }

    internal LocalIndexerDefinitionProvider(
        string definitionsDirectory,
        ILogger<LocalIndexerDefinitionProvider> logger
    )
    {
        DefinitionsDirectory = Path.GetFullPath(definitionsDirectory);
        _logger = logger;
    }

    public string Name => "local";

    public string DefinitionsDirectory { get; }

    public async Task<IReadOnlyList<IndexerDefinitionSource>> LoadAsync(
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(DefinitionsDirectory);
        var paths = Directory
            .EnumerateFiles(DefinitionsDirectory, "*.yml", SearchOption.TopDirectoryOnly)
            .Concat(
                Directory.EnumerateFiles(
                    DefinitionsDirectory,
                    "*.yaml",
                    SearchOption.TopDirectoryOnly
                )
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count > MaximumDefinitions)
        {
            throw new InvalidDataException(
                $"The local definition directory contains more than {MaximumDefinitions} YAML files."
            );
        }

        var sources = new List<IndexerDefinitionSource>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogWarning(
                    "Skipping Cardigann definition symlink {DefinitionFile}",
                    info.Name
                );
                continue;
            }

            if (info.Length > MaximumDefinitionBytes)
            {
                sources.Add(
                    new IndexerDefinitionSource(
                        path,
                        string.Empty,
                        $"Definition exceeds {MaximumDefinitionBytes / 1024} KiB."
                    )
                );
                continue;
            }

            var yaml = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            sources.Add(new IndexerDefinitionSource(path, yaml));
        }

        return sources;
    }
}
