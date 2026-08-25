using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using NebulaBridge.Services;

namespace NebulaBridge.Providers;

public sealed class NebulaBridgeImageProvider(
    ILogger<NebulaBridgeImageProvider> log,
    NebulaBridgeMetadataService metadata
)
    : IRemoteImageProvider,
        IHasOrder
{
    public string Name => "Nebula Bridge";
    public int Order => 0;

    public bool Supports(BaseItem item) => item is Movie or Series;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) =>
        [ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken
    )
    {
        var id = ResolveId(item);
        if (id is null)
        {
            log.LogDebug("Nebula Bridge image provider: no usable ID for {Name}", item.Name);
            return [];
        }

        var configuration = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);

        var mediaType = item is Movie ? StremioMediaType.Movie : StremioMediaType.Series;
        StremioMeta? meta;
        try
        {
            meta = await metadata
                .GetMetaAsync(configuration, id, mediaType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nebula Bridge image provider: failed to fetch meta for {Id}", id);
            return [];
        }

        if (meta is null || !meta.IsValid())
            return [];

        return BuildImages(meta);
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    private static IEnumerable<RemoteImageInfo> BuildImages(StremioMeta meta)
    {
        var images = new List<RemoteImageInfo>();

        if (!string.IsNullOrWhiteSpace(meta.Poster))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Nebula Bridge",
                    Type = ImageType.Primary,
                    Url = meta.Poster,
                }
            );

        if (!string.IsNullOrWhiteSpace(meta.Background))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Nebula Bridge",
                    Type = ImageType.Backdrop,
                    Url = meta.Background,
                }
            );

        if (!string.IsNullOrWhiteSpace(meta.Logo))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Nebula Bridge",
                    Type = ImageType.Logo,
                    Url = meta.Logo,
                }
            );

        if (!string.IsNullOrWhiteSpace(meta.LandscapePoster))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Nebula Bridge",
                    Type = ImageType.Thumb,
                    Url = meta.LandscapePoster,
                }
            );

        return images;
    }

    private static string? ResolveId(BaseItem item)
    {
        var imdb = item.GetProviderId(MetadataProvider.Imdb);
        if (!string.IsNullOrWhiteSpace(imdb))
            return imdb;

        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(tmdb))
            return $"tmdb:{tmdb}";

        return null;
    }
}
