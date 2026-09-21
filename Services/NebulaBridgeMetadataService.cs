using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

/// <summary>
/// Metadata facade. Native TMDB is authoritative; a configured Stremio endpoint remains an
/// optional compatibility fallback while older installations migrate.
/// </summary>
public sealed class NebulaBridgeMetadataService(
    NativeTmdbClient tmdb,
    ILogger<NebulaBridgeMetadataService> logger
)
{
    public async Task<StremioMeta?> GetMetaAsync(
        PluginConfiguration configuration,
        string id,
        StremioMediaType mediaType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var meta = await tmdb.GetMetaAsync(id, mediaType, cancellationToken)
                .ConfigureAwait(false);
            if (
                meta is not null
                && (
                    mediaType != StremioMediaType.Series
                    || meta.Videos is { Count: > 0 }
                    || configuration.Stremio is null
                )
            )
            {
                return meta;
            }

            if (meta is not null && mediaType == StremioMediaType.Series)
            {
                logger.LogWarning(
                    "Native TMDB returned no episodes for {Id}; trying legacy metadata fallback",
                    id
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native TMDB metadata failed for {Id}; trying legacy fallback", id);
        }

        if (configuration.Stremio is null)
        {
            return null;
        }

        return await configuration.Stremio.GetMetaAsync(id, mediaType).ConfigureAwait(false);
    }

    public Task<StremioMeta?> GetMetaAsync(
        PluginConfiguration configuration,
        BaseItem item,
        CancellationToken cancellationToken
    )
    {
        var id = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(id))
        {
            return GetMetaAsync(
                configuration,
                $"tmdb:{id}",
                item.GetBaseItemKind().ToStremio(),
                cancellationToken
            );
        }

        id = item.GetProviderId(MetadataProvider.Imdb);
        return string.IsNullOrWhiteSpace(id)
            ? Task.FromResult<StremioMeta?>(null)
            : GetMetaAsync(
                configuration,
                id,
                item.GetBaseItemKind().ToStremio(),
                cancellationToken
            );
    }

    public async Task<IReadOnlyList<StremioMeta>> SearchAsync(
        PluginConfiguration configuration,
        string query,
        StremioMediaType mediaType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await tmdb.SearchAsync(query, mediaType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native TMDB search failed; trying legacy fallback");
        }

        return configuration.Stremio is null
            ? []
            : await configuration.Stremio.SearchAsync(query, mediaType).ConfigureAwait(false);
    }

    public async Task EnrichDigitalReleaseDateAsync(
        PluginConfiguration configuration,
        StremioMeta meta,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await tmdb.EnrichDigitalReleaseDateAsync(meta, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Native TMDB release-date enrichment failed for {Id}", meta.Id);
            if (configuration.Stremio is not null)
            {
                await configuration.Stremio.EnrichDigitalReleaseDateAsync(meta, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
