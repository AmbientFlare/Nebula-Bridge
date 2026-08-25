using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public interface IIndexerSearchEngine
{
    Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(
        IndexerDefinition definition,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    );
}

public sealed class CardigannSearchCoordinator(
    IndexerDefinitionLoader definitionLoader,
    IIndexerSearchEngine searchEngine,
    NativeReleaseAggregator aggregator,
    ILogger<CardigannSearchCoordinator> logger
)
{
    private static readonly TimeSpan PerIndexerTimeout = TimeSpan.FromSeconds(15);

    public async Task<NativeSearchResult> SearchAsync(
        NativeMediaQuery query,
        string? definitionId,
        CancellationToken cancellationToken
    )
    {
        var definitions = string.IsNullOrWhiteSpace(definitionId)
            ? definitionLoader.GetEnabledDefinitions()
            : [definitionLoader.GetRequired(definitionId)];
        var searches = definitions.Select(definition =>
            SearchOneAsync(definition, query, cancellationToken)
        );
        var completed = await Task.WhenAll(searches).ConfigureAwait(false);
        var candidates = completed.SelectMany(result => result.Candidates).ToList();
        var failures = completed
            .Where(result => result.Failure is not null)
            .Select(result => result.Failure!)
            .ToList();
        var output = aggregator.Aggregate(candidates, perSourceLimit: 100, globalLimit: 200);
        return new NativeSearchResult(output, failures);
    }

    private async Task<IndexerSearchOutcome> SearchOneAsync(
        IndexerDefinition definition,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerIndexerTimeout);
        try
        {
            var candidates = await searchEngine
                .SearchAsync(definition, query, timeout.Token)
                .ConfigureAwait(false);
            return new(candidates, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Indexer search timeout: {IndexerId}", definition.Id);
            return new(
                [],
                new NativeSourceFailure(
                    definition.Id,
                    "Request exceeded the per-indexer timeout.",
                    definition.Name,
                    "request",
                    "timeout"
                )
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Indexer search failed: {IndexerId}", definition.Id);
            return new(
                [],
                new NativeSourceFailure(
                    definition.Id,
                    "The indexer query failed.",
                    definition.Name,
                    ex is FormatException
                        or InvalidDataException
                        or System.Text.Json.JsonException
                        or System.Xml.XmlException
                        ? "parse"
                        : "request",
                    ex.GetType().Name.ToLowerInvariant()
                )
            );
        }
    }

    private sealed record IndexerSearchOutcome(
        IReadOnlyList<NativeReleaseCandidate> Candidates,
        NativeSourceFailure? Failure
    );
}
