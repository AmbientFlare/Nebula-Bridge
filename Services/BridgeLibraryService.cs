using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

public sealed record BridgeLibraryDescriptor(
    string Key,
    string Name,
    string Path,
    CollectionTypeOptions CollectionType
);

/// <summary>
/// Owns the stable top-level Jellyfin libraries created by Nebula Bridge.
/// </summary>
public sealed class BridgeLibraryService(
    IApplicationPaths appPaths,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    NebulaBridgeManager manager,
    ILogger<BridgeLibraryService> logger
)
{
    public const string LibraryNamePrefix = "Nebula Bridge — ";
    private readonly SemaphoreSlim _libraryLock = new(1, 1);
    private string RootPath => Path.Combine(appPaths.DataPath, "nebulabridge", "libraries");

    public BridgeLibraryDescriptor GetCatalogDescriptor(CatalogConfig catalog)
    {
        var key = GetCatalogLibraryKey(catalog);
        var displayName = key switch
        {
            "trending" => "Trending",
            "popular" => "Popular",
            "anticipated" => "Anticipated",
            "box-office" => "Box Office",
            "next-episodes" => "Trakt Next Episodes",
            _ => string.IsNullOrWhiteSpace(catalog.Name) ? catalog.Id : catalog.Name,
        };
        var collectionType = key is "trending" or "popular" or "anticipated" or "box-office"
            ? CollectionTypeOptions.mixed
            : catalog.Type.Equals("series", StringComparison.OrdinalIgnoreCase)
                ? CollectionTypeOptions.tvshows
                : CollectionTypeOptions.movies;
        return Descriptor($"catalog-{key}", displayName, collectionType);
    }

    public BridgeLibraryDescriptor GetNextEpisodesDescriptor() =>
        Descriptor("catalog-next-episodes", "Trakt Next Episodes", CollectionTypeOptions.tvshows);

    public BridgeLibraryDescriptor GetPromotionDescriptor(bool series) =>
        Descriptor(
            series ? "promoted-shows" : "promoted-movies",
            series ? "Saved Shows" : "Saved Movies",
            series ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies
        );

    public BridgeLibraryDescriptor GetDiscoveryDescriptor(bool series) =>
        Descriptor("search-results", "Search Results", CollectionTypeOptions.mixed);

    public async Task<bool> ConsolidateLegacyDiscoveryLibrariesAsync(
        CancellationToken cancellationToken
    )
    {
        var target = await EnsureLibraryAsync(
                GetDiscoveryDescriptor(series: false),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (target is null)
        {
            return false;
        }

        var legacy = new[]
        {
            (Name: LibraryNamePrefix + "Discovery Movies", Path: Path.Combine(RootPath, "discovery-movies")),
            (Name: LibraryNamePrefix + "Discovery Shows", Path: Path.Combine(RootPath, "discovery-shows")),
        };
        foreach (var entry in legacy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = manager.TryGetFolderByPath(entry.Path);
            if (source is not null)
            {
                var items = libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            ParentId = source.Id,
                            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                            Recursive = false,
                            IsDeadPerson = true,
                        }
                    )
                    .ToList();
                foreach (var item in items)
                {
                    item.SetParent(target);
                    await item
                        .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (items.Count > 0)
                {
                    logger.LogInformation(
                        "Moved {Count} item(s) from {OldLibrary} into {NewLibrary}",
                        items.Count,
                        entry.Name,
                        target.Name
                    );
                }
            }

            var virtualFolder = libraryManager
                .GetVirtualFolders()
                .FirstOrDefault(folder =>
                    folder.Locations.Contains(entry.Path, StringComparer.Ordinal)
                );
            if (virtualFolder is not null)
            {
                await libraryManager
                    .RemoveVirtualFolder(virtualFolder.Name, refreshLibrary: true)
                    .ConfigureAwait(false);
                logger.LogInformation(
                    "Removed superseded managed library {LibraryName}; its data directory was retained",
                    virtualFolder.Name
                );
            }
        }

        return true;
    }

    public async Task<Folder?> EnsureLibraryAsync(
        BridgeLibraryDescriptor descriptor,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(descriptor.Path);
        var seedPath = Path.Combine(descriptor.Path, "stub.txt");
        if (!File.Exists(seedPath))
        {
            await File.WriteAllTextAsync(
                    seedPath,
                    "Nebula Bridge keeps this file so Jellyfin retains the managed library path.",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        if (manager.TryGetFolderByPath(descriptor.Path) is { } existing)
        {
            await EnsureLibraryArtworkAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await _libraryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (manager.TryGetFolderByPath(descriptor.Path) is { } found)
            {
                await EnsureLibraryArtworkAsync(descriptor, cancellationToken).ConfigureAwait(false);
                return found;
            }

            var virtualFolder = libraryManager
                .GetVirtualFolders()
                .FirstOrDefault(folder =>
                    folder.Locations.Contains(descriptor.Path, StringComparer.Ordinal)
                );
            if (virtualFolder is null)
            {
                await libraryManager
                    .AddVirtualFolder(
                        descriptor.Name,
                        descriptor.CollectionType,
                        new LibraryOptions
                        {
                            EnableRealtimeMonitor = false,
                            SaveLocalMetadata = false,
                            PathInfos = [new MediaPathInfo(descriptor.Path)],
                        },
                        refreshLibrary: true
                    )
                    .ConfigureAwait(false);
                logger.LogInformation(
                    "Created stable Nebula Bridge library {LibraryName} at {LibraryPath}",
                    descriptor.Name,
                    descriptor.Path
                );
            }

            var result = manager.TryGetFolderByPath(descriptor.Path);
            await EnsureLibraryArtworkAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _libraryLock.Release();
        }
    }

    private async Task EnsureLibraryArtworkAsync(
        BridgeLibraryDescriptor descriptor,
        CancellationToken cancellationToken
    )
    {
        var folderId = GetVirtualFolderId(descriptor);
        if (folderId is null || libraryManager.GetItemById(folderId.Value) is not { } item)
        {
            return;
        }

        var primaryImage = item.GetImageInfo(ImageType.Primary, 0);
        if (primaryImage is not null && !IsLegacyBlankArtwork(primaryImage.Path))
        {
            return;
        }

        const string resourceName = "NebulaBridge.Assets.nebula-library-cover.png";
        await using var image = typeof(BridgeLibraryService).Assembly.GetManifestResourceStream(
            resourceName
        );
        if (image is null)
        {
            logger.LogWarning("Managed-library artwork resource {ResourceName} is missing", resourceName);
            return;
        }

        await providerManager
            .SaveImage(
                item,
                image,
                "image/png",
                ImageType.Primary,
                null,
                cancellationToken
            )
            .ConfigureAwait(false);
        logger.LogInformation("Applied managed-library artwork to {LibraryName}", descriptor.Name);
    }

    internal static bool IsLegacyBlankArtwork(string? path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || !Path.GetFileName(path).Equals("poster.png", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            // Earlier builds generated a 960x540 solid-black PNG (3,277 bytes) for
            // managed libraries. Keep user-supplied artwork, but repair that tiny
            // metadata placeholder whenever the library is next ensured.
            return file.Exists && file.Length is > 0 and <= 4096;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlySet<Guid> GetManagedVirtualFolderIds()
    {
        var root = Path.GetFullPath(RootPath) + Path.DirectorySeparatorChar;
        return libraryManager
            .GetVirtualFolders()
            .Where(folder => folder.Locations.Any(location => IsUnderRoot(location, root)))
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }

    public IReadOnlyList<Guid> GetVisibleVirtualFolderIds() => libraryManager
        .GetVirtualFolders()
        .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .Distinct()
        .ToList();

    public Guid? GetVirtualFolderId(BridgeLibraryDescriptor descriptor)
    {
        var folder = libraryManager
            .GetVirtualFolders()
            .FirstOrDefault(item =>
                item.Locations.Contains(descriptor.Path, StringComparer.Ordinal)
            );
        return folder is not null && Guid.TryParse(folder.ItemId, out var id) ? id : null;
    }

    public static string GetCatalogLibraryKey(CatalogConfig catalog)
    {
        var id = catalog.Id.ToLowerInvariant();
        if (id.Contains("next", StringComparison.Ordinal))
            return "next-episodes";
        if (id.Contains("trending", StringComparison.Ordinal))
            return "trending";
        if (id.Contains("popular", StringComparison.Ordinal))
            return "popular";
        if (id.Contains("anticipated", StringComparison.Ordinal))
            return "anticipated";
        if (id.Contains("box-office", StringComparison.Ordinal) || id.Contains("boxoffice", StringComparison.Ordinal))
            return "box-office";

        var raw = $"{catalog.Source}-{catalog.Id}".ToLowerInvariant();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    public static CatalogRefreshCadence GetCadence(CatalogConfig catalog)
    {
        var key = GetCatalogLibraryKey(catalog);
        return key switch
        {
            "next-episodes" => CatalogRefreshCadence.TwiceDaily,
            "trending" or "box-office" => CatalogRefreshCadence.Daily,
            _ => CatalogRefreshCadence.Weekly,
        };
    }

    private BridgeLibraryDescriptor Descriptor(
        string key,
        string label,
        CollectionTypeOptions collectionType
    ) =>
        new(
            key,
            LibraryNamePrefix + label,
            Path.Combine(RootPath, key),
            collectionType
        );

    private static bool IsUnderRoot(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var fullPath = Path.GetFullPath(candidate);
        return (fullPath + Path.DirectorySeparatorChar).StartsWith(
            root,
            StringComparison.Ordinal
        );
    }
}

public enum CatalogRefreshCadence
{
    TwiceDaily,
    Daily,
    Weekly,
}
