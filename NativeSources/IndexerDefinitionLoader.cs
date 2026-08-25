using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public sealed class IndexerDefinitionLoader(
    IIndexerDefinitionProvider provider,
    CardigannDefinitionParser parser,
    IIndexerPreferenceStore preferences,
    ILogger<IndexerDefinitionLoader> logger
)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IndexerDefinitionSnapshot _snapshot = IndexerDefinitionSnapshot.Empty;

    public string DefinitionsDirectory => provider.DefinitionsDirectory;

    public IReadOnlyList<NativeDefinitionSummary> GetSummaries() =>
        GetAllSummaries()
            .Where(summary => summary.Compatible && summary.Enabled)
            .ToList();

    public IReadOnlyList<NativeDefinitionSummary> GetAllSummaries()
    {
        EnsureLoaded();
        var enabled = GetEnabledIds();
        return _snapshot
            .Records.Values.OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(record => ToSummary(record, enabled.Contains(record.Id)))
            .ToList();
    }

    public IReadOnlyList<IndexerDefinition> GetEnabledDefinitions()
    {
        EnsureLoaded();
        var enabled = GetEnabledIds();
        return _snapshot
            .Records.Values.Where(record =>
                record.Compatible
                && record.Definition is not null
                && enabled.Contains(record.Id)
            )
            .Select(record => record.Definition!)
            .OrderBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IndexerDefinition GetRequired(string id)
    {
        EnsureLoaded();
        if (
            !_snapshot.Records.TryGetValue(id, out var record)
            || record.Definition is null
            || !record.Compatible
            || !GetEnabledIds().Contains(record.Id)
        )
        {
            throw new KeyNotFoundException(
                $"Unknown, disabled, or incompatible Cardigann indexer '{id}'."
            );
        }

        return record.Definition;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        EnsureLoaded();
        if (
            !_snapshot.Records.TryGetValue(id, out var record)
            || record.Definition is null
            || !record.Compatible
        )
        {
            return false;
        }

        return preferences.SetEnabled(record.Id, enabled);
    }

    public IndexerRefreshResponse GetStatus()
    {
        EnsureLoaded();
        return BuildResponse(true, "Local Cardigann v11 definitions loaded.");
    }

    public async Task<IndexerRefreshResponse> RefreshAsync(
        CancellationToken cancellationToken
    )
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<IndexerDefinitionSource> sources;
            try
            {
                sources = await provider.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException
            )
            {
                logger.LogError(ex, "Could not rescan local Cardigann definitions");
                return BuildResponse(false, $"Definition rescan failed: {ex.Message}");
            }

            var records = new Dictionary<string, IndexerDefinitionRecord>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = source.Error is null
                    ? parser.Parse(source.Yaml, source.Path, source.DefinitionVersion)
                    : new IndexerDefinitionRecord(
                        Path.GetFileNameWithoutExtension(source.Path),
                        Path.GetFileNameWithoutExtension(source.Path),
                        string.Empty,
                        source.Path,
                        null,
                        false,
                        false,
                        source.Error,
                        source.DefinitionVersion
                    );
                if (records.ContainsKey(record.Id))
                {
                    var duplicateId = $"invalid:{Path.GetFileName(source.Path)}";
                    record = record with
                    {
                        Id = duplicateId,
                        Compatible = false,
                        Error = $"Duplicate indexer id '{record.Id}'.",
                    };
                }

                records[record.Id] = record;
                if (!record.Loaded)
                {
                    logger.LogWarning(
                        "Indexer definition invalid: {DefinitionFile} — {DefinitionError}",
                        Path.GetFileName(source.Path),
                        record.Error
                    );
                }
                else if (!record.Compatible)
                {
                    logger.LogWarning(
                        "Indexer definition incompatible: {IndexerId} — {DefinitionError}",
                        record.Id,
                        record.Error
                    );
                }
                else
                {
                    logger.LogInformation(
                        "Indexer definition loaded: {IndexerId} (Cardigann v{SchemaVersion})",
                        record.Id,
                        IndexerDefinition.SupportedSchemaVersion
                    );
                }
            }

            _snapshot = new IndexerDefinitionSnapshot(records, DateTime.UtcNow);
            var compatible = records.Values.Count(record => record.Compatible);
            var invalid = records.Values.Count(record => !record.Loaded);
            logger.LogInformation(
                "Indexer definitions refreshed: {LoadedCount} loaded, {CompatibleCount} compatible, {InvalidCount} invalid",
                records.Values.Count(record => record.Loaded),
                compatible,
                invalid
            );
            return BuildResponse(
                true,
                $"Rescan complete: {records.Count} definition(s), {compatible} compatible."
            );
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public (IndexerDefinition? Definition, IReadOnlyList<string> Errors) Parse(string yaml)
    {
        var record = parser.Parse(yaml, "inline.yml");
        return record.Definition is not null && record.Compatible
            ? (record.Definition, [])
            : (null, [record.Error ?? "Definition is invalid."]);
    }

    private void EnsureLoaded()
    {
        if (_snapshot.RefreshedUtc == DateTime.MinValue)
        {
            RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private HashSet<string> GetEnabledIds() =>
        new(
            preferences.GetEnabledIds(),
            StringComparer.OrdinalIgnoreCase
        );

    private static NativeDefinitionSummary ToSummary(
        IndexerDefinitionRecord record,
        bool enabled
    )
    {
        var effectiveEnabled = enabled && record.Compatible;
        var state = !record.Loaded
            ? "invalid"
            : !record.Compatible
                ? "unsupported"
                : effectiveEnabled
                    ? "enabled"
                    : "disabled";
        return new NativeDefinitionSummary(
            record.Id,
            record.Name,
            record.Description,
            "local",
            effectiveEnabled,
            record.Loaded,
            record.Compatible,
            state,
            record.Error,
            record.DefinitionVersion,
            record.Definition?.Language ?? string.Empty,
            record.Definition?.Type ?? string.Empty
        );
    }

    private IndexerRefreshResponse BuildResponse(bool success, string message)
    {
        var records = _snapshot.Records.Values;
        return new IndexerRefreshResponse(
            success,
            message,
            records.Count(),
            _snapshot.RefreshedUtc == DateTime.MinValue ? null : _snapshot.RefreshedUtc,
            records.Count(record => record.Loaded),
            records.Count(record => record.Compatible),
            records.Count(record => !record.Loaded),
            provider.DefinitionsDirectory
        );
    }
}
