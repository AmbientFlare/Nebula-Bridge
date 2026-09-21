using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Decorators;

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
    IImageProcessor imageProcessor,
    IMediaEncoder mediaEncoder,
    Lazy<ProviderManagerDecorator> providerDecorator,
    NebulaBridgeManager manager,
    ILogger<BridgeLibraryService> logger
)
{
    public const string LibraryNamePrefix = "Nebula Bridge — ";
    private const int CollagePosterCount = 5;
    private const int CollageWidth = 960;
    private const int CollageHeight = 540;

    /// <summary>1 = horizontal strip of poster slices.</summary>
    private const int CollageLayout = 1;
    private static readonly TimeSpan CollageRefreshInterval = TimeSpan.FromHours(12);
    private readonly SemaphoreSlim _libraryLock = new(1, 1);
    private string RootPath => Path.Combine(appPaths.DataPath, "nebulabridge", "libraries");
    private string ArtworkPath => Path.Combine(appPaths.DataPath, "nebulabridge", "artwork");

    public const string MoviesKey = "movies";
    public const string ShowsKey = "shows";
    public const string NextEpisodesKey = "catalog-next-episodes";
    public const string SearchResultsKey = "search-results";

    /// <summary>
    /// The shared library every non-separate source of a type feeds. Sources are merged here
    /// and deduplicated by identity path, so a title listed by several feeds appears once.
    /// </summary>
    public BridgeLibraryDescriptor GetAggregateDescriptor(bool series) =>
        Descriptor(
            series ? ShowsKey : MoviesKey,
            series ? "Shows" : "Movies",
            series ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies
        );

    public BridgeLibraryDescriptor GetCatalogDescriptor(CatalogConfig catalog)
    {
        if (IsNextEpisodes(catalog))
        {
            return GetNextEpisodesDescriptor();
        }

        var series = IsSeries(catalog);
        if (!catalog.SeparateLibrary)
        {
            return GetAggregateDescriptor(series);
        }

        var displayName = string.IsNullOrWhiteSpace(catalog.Name) ? catalog.Id : catalog.Name;
        return Descriptor(
            $"catalog-{GetCatalogSlug(catalog)}",
            displayName,
            series ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies
        );
    }

    public BridgeLibraryDescriptor GetNextEpisodesDescriptor() =>
        Descriptor(NextEpisodesKey, "Trakt Next Episodes", CollectionTypeOptions.tvshows);

    public BridgeLibraryDescriptor GetDiscoveryDescriptor(bool series) =>
        Descriptor(SearchResultsKey, "Search Results", CollectionTypeOptions.mixed);

    /// <summary>
    /// Every managed library the current configuration still wants. Anything else under the
    /// managed root is a leftover from an earlier layout and gets folded into these.
    /// </summary>
    public IReadOnlyList<BridgeLibraryDescriptor> GetActiveDescriptors()
    {
        var cfg = NebulaBridgePlugin.Instance?.Configuration;
        var active = new List<BridgeLibraryDescriptor>
        {
            GetAggregateDescriptor(series: false),
            GetAggregateDescriptor(series: true),
            GetNextEpisodesDescriptor(),
            GetDiscoveryDescriptor(series: false),
        };
        foreach (var catalog in cfg?.Catalogs ?? [])
        {
            if (catalog.Enabled && catalog.SeparateLibrary && !IsNextEpisodes(catalog))
            {
                active.Add(GetCatalogDescriptor(catalog));
            }
        }

        return active;
    }

    /// <summary>
    /// Folds every superseded managed library (per-catalog Trending/Popular folders, Saved
    /// Movies/Shows, the old Discovery pair, and any separate catalog folder that has since
    /// been switched back to shared) into the libraries the current layout uses, then removes
    /// the empty virtual folder. Data directories are retained.
    /// </summary>
    public async Task<bool> ConsolidateLegacyLibrariesAsync(CancellationToken cancellationToken)
    {
        var movies = await EnsureLibraryAsync(GetAggregateDescriptor(series: false), cancellationToken)
            .ConfigureAwait(false);
        var shows = await EnsureLibraryAsync(GetAggregateDescriptor(series: true), cancellationToken)
            .ConfigureAwait(false);
        var search = await EnsureLibraryAsync(GetDiscoveryDescriptor(series: false), cancellationToken)
            .ConfigureAwait(false);
        if (movies is null || shows is null || search is null)
        {
            return false;
        }

        var activePaths = GetActiveDescriptors()
            .Select(descriptor => Path.GetFullPath(descriptor.Path))
            .ToHashSet(StringComparer.Ordinal);
        var root = Path.GetFullPath(RootPath) + Path.DirectorySeparatorChar;
        var legacy = libraryManager
            .GetVirtualFolders()
            .Where(folder => folder.Locations.Any(location => IsUnderRoot(location, root)))
            .Where(folder => !folder.Locations.Any(location => activePaths.Contains(Path.GetFullPath(location))))
            .ToList();
        foreach (var virtualFolder in legacy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileName(virtualFolder.Locations.First(location => IsUnderRoot(location, root)).TrimEnd(Path.DirectorySeparatorChar));
            var isDiscovery = key.StartsWith("discovery-", StringComparison.Ordinal);
            foreach (var location in virtualFolder.Locations)
            {
                var source = manager.TryGetFolderByPath(location);
                if (source is null)
                {
                    continue;
                }

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
                    // Resolved-stream alternates are a playback cache rebuilt on demand; they
                    // are dropped with the old folder rather than merged into the title.
                    .Where(item => !item.Tags.Contains(NebulaBridgeManager.StreamTag, StringComparer.Ordinal))
                    .ToList();
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (target, scope) = isDiscovery
                        ? (search, SearchResultsKey)
                        : item is Series
                            ? (shows, ShowsKey)
                            : (movies, MoviesKey);
                    await manager
                        .MoveManagedItemAsync(item, target, scope, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (items.Count > 0)
                {
                    logger.LogInformation(
                        "Folded {Count} item(s) from {OldLibrary} into the current Nebula Bridge libraries",
                        items.Count,
                        virtualFolder.Name
                    );
                }
            }

            // Each refreshLibrary:true removal would cancel the scan queued by the previous
            // one, so remove quietly and queue a single scan once every legacy folder is gone.
            await libraryManager
                .RemoveVirtualFolder(virtualFolder.Name, refreshLibrary: false)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Removed superseded managed library {LibraryName}; its data directory was retained",
                virtualFolder.Name
            );
        }

        if (legacy.Count > 0)
        {
            libraryManager.QueueLibraryScan();
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

    /// <summary>
    /// Re-tiles the folder artwork of every active managed library from its current
    /// contents. Called after imports so the collage keeps up with what is inside.
    /// </summary>
    public async Task RefreshArtworkAsync(CancellationToken cancellationToken)
    {
        foreach (var descriptor in GetActiveDescriptors())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EnsureLibraryArtworkAsync(descriptor, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh artwork for {LibraryName}", descriptor.Name);
            }
        }
    }

    /// <summary>
    /// A managed library's poster is a collage of a few random titles it holds, in the same
    /// style Jellyfin gives native libraries. An empty library shows the Nebula Bridge cover
    /// instead. Only artwork this plugin wrote is ever replaced, so a poster the user uploads
    /// through the dashboard stays put.
    /// </summary>
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

        var state = ReadArtworkState(descriptor.Key);
        var primaryImage = item.GetImageInfo(ImageType.Primary, 0);
        // Earlier builds wrote the cover without recording it; recognise it by content.
        var owned =
            primaryImage is null
            || IsLegacyBlankArtwork(primaryImage.Path)
            || IsOwnedArtwork(primaryImage.Path, state)
            || IsOwnedArtwork(primaryImage.Path, EmbeddedCoverState);
        if (!owned)
        {
            return;
        }

        var candidates = GetCollageCandidates(descriptor, folderId.Value);
        // Re-tile periodically so the random pick rotates; an empty library keeps its
        // cover until something arrives. Decide before fetching any poster bytes.
        var hadCollage = state is { PosterCount: > 0 };
        if (
            primaryImage is not null
            && !IsLegacyBlankArtwork(primaryImage.Path)
            && state is not null
            && (candidates.Count > 0) == hadCollage
            && (!hadCollage || (state.Layout == CollageLayout && DateTimeOffset.UtcNow - state.WrittenAt < CollageRefreshInterval))
        )
        {
            return;
        }

        var posters = await PickPosterPathsAsync(candidates, cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0 && posters.Count == 0)
        {
            logger.LogWarning(
                "No poster of {LibraryName}'s {Count} title(s) could be fetched for the collage; keeping current artwork",
                descriptor.Name,
                candidates.Count
            );
            return;
        }

        byte[] bytes;
        if (posters.Count > 0)
        {
            var collage = await BuildCollageAsync(descriptor, posters, cancellationToken).ConfigureAwait(false);
            if (collage is null)
            {
                return;
            }

            bytes = collage;
        }
        else
        {
            var cover = LoadEmbeddedCover();
            if (cover is null)
            {
                return;
            }

            bytes = cover;
        }

        logger.LogDebug("Collage: saving {Bytes}-byte artwork to {LibraryName}", bytes.Length, descriptor.Name);
        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            await providerManager
                .SaveImage(item, stream, "image/png", ImageType.Primary, null, cancellationToken)
                .ConfigureAwait(false);
        }

        WriteArtworkState(
            descriptor.Key,
            new ArtworkState(
                Convert.ToHexString(SHA256.HashData(bytes)),
                posters.Count,
                DateTimeOffset.UtcNow,
                CollageLayout
            )
        );
        logger.LogInformation(
            "Applied {Kind} artwork to {LibraryName}",
            posters.Count > 0 ? $"{posters.Count}-poster collage" : "default",
            descriptor.Name
        );
    }

    /// <summary>The library's real (non-stream) movies and series that carry a primary image.</summary>
    private IReadOnlyList<BaseItem> GetCollageCandidates(BridgeLibraryDescriptor descriptor, Guid libraryId)
    {
        // Titles are filed under the library's data folder, which is what Jellyfin records
        // as their top parent; the virtual folder id is included for items scanned in directly.
        var topParents = new List<Guid> { libraryId };
        if (manager.TryGetFolderByPath(descriptor.Path) is { } dataFolder)
        {
            topParents.Add(dataFolder.Id);
        }

        return libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    TopParentIds = topParents.ToArray(),
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                    IsVirtualItem = false,
                    ImageTypes = [ImageType.Primary],
                    IsDeadPerson = true,
                }
            )
            .Where(item => item.Tags?.Contains(NebulaBridgeManager.StreamTag, StringComparer.Ordinal) != true)
            .Where(item => !string.IsNullOrWhiteSpace(item.GetImageInfo(ImageType.Primary, 0)?.Path))
            .ToList();
    }

    /// <summary>A random handful of poster files, fetching lazy ones as needed.</summary>
    private async Task<IReadOnlyList<string>> PickPosterPathsAsync(
        IReadOnlyList<BaseItem> candidates,
        CancellationToken cancellationToken
    )
    {
        var shuffled = candidates.ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shuffled));

        var posters = new List<string>();
        var attempts = 0;
        foreach (var item in shuffled.Take(CollagePosterCount * 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            var path = await ResolvePosterPathAsync(item, cancellationToken).ConfigureAwait(false);
            if (path is not null && !posters.Contains(path, StringComparer.Ordinal))
            {
                posters.Add(path);
            }

            if (posters.Count >= CollagePosterCount)
            {
                break;
            }
        }

        logger.LogDebug(
            "Collage picked {Picked} poster(s) from {Attempts} of {Candidates} candidate(s)",
            posters.Count,
            attempts,
            candidates.Count
        );
        return posters;
    }

    /// <summary>
    /// Lazy-image mode leaves a zero-byte placeholder with a <c>.url</c> sidecar until a client
    /// first renders the poster. The collage needs real pixels, so fetch it now the same way the
    /// image pipeline would on first view.
    /// </summary>
    private async Task<string?> ResolvePosterPathAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var info = item.GetImageInfo(ImageType.Primary, 0);
        if (info?.Path is not { } path)
        {
            return null;
        }

        if (HasPixels(path))
        {
            return path;
        }

        var urlFile = path + ".url";
        if (!File.Exists(urlFile))
        {
            urlFile = ProviderManagerDecorator.BuildImageInfo(appPaths, item.Id, ImageType.Primary, null).Path + ".url";
        }

        if (!File.Exists(urlFile))
        {
            return null;
        }

        try
        {
            var url = (await File.ReadAllTextAsync(urlFile, cancellationToken).ConfigureAwait(false)).Trim();
            logger.LogDebug("Collage: fetching poster for {Name}", item.Name);
            await providerDecorator.Value
                .SaveImageDirect(item, url, ImageType.Primary, null, cancellationToken)
                .ConfigureAwait(false);
            logger.LogDebug("Collage: fetched poster for {Name}; updating images", item.Name);
            await libraryManager.UpdateImagesAsync(item).ConfigureAwait(false);
            logger.LogDebug("Collage: images updated for {Name}", item.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch the poster for {Name} ({ErrorType})", item.Name, ex.GetType().Name);
            return null;
        }

        var fresh = item.GetImageInfo(ImageType.Primary, 0)?.Path;
        if (fresh is not null && HasPixels(fresh))
        {
            return fresh;
        }

        logger.LogDebug("Fetched poster for {Name} but no image file followed ({Path})", item.Name, fresh);
        return null;
    }

    private static bool HasPixels(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tiles the posters side by side as vertical slices, each showing the middle of its
    /// poster, the way most media apps preview a folder. Jellyfin's own collage builder
    /// (one backdrop with the library name) is the fallback when ffmpeg is unavailable.
    /// </summary>
    private async Task<byte[]?> BuildCollageAsync(
        BridgeLibraryDescriptor descriptor,
        IReadOnlyList<string> posters,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(ArtworkPath);
        var output = Path.Combine(ArtworkPath, descriptor.Key + ".png");
        try
        {
            logger.LogDebug("Collage: tiling {Count} poster(s) for {LibraryName}", posters.Count, descriptor.Name);
            var tiled = await TilePostersAsync(posters, output, cancellationToken).ConfigureAwait(false);
            if (!tiled)
            {
                imageProcessor.CreateImageCollage(
                    new ImageCollageOptions
                    {
                        InputPaths = posters.ToArray(),
                        OutputPath = output,
                        Width = CollageWidth,
                        Height = CollageHeight,
                    },
                    descriptor.Name[LibraryNamePrefix.Length..]
                );
            }

            var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            // Skia paints a plain black canvas when none of the inputs decoded; that
            // compresses to a few KB and is worse than the cover we already have.
            if (bytes.Length <= 4096)
            {
                logger.LogWarning("Poster collage for {LibraryName} came out blank; keeping current artwork", descriptor.Name);
                return null;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not build a poster collage for {LibraryName}", descriptor.Name);
            return null;
        }
    }

    /// <summary>
    /// Runs Jellyfin's bundled ffmpeg to scale every poster to the collage height, crop its
    /// centre to an equal-width slice, and stack the slices horizontally.
    /// </summary>
    private async Task<bool> TilePostersAsync(
        IReadOnlyList<string> posters,
        string output,
        CancellationToken cancellationToken
    )
    {
        var encoder = mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(encoder) || !File.Exists(encoder))
        {
            return false;
        }

        var sliceWidth = CollageWidth / posters.Count;
        var filter = new System.Text.StringBuilder();
        var inputs = new System.Text.StringBuilder();
        for (var i = 0; i < posters.Count; i++)
        {
            // The last slice absorbs the rounding remainder so the strip is exactly CollageWidth wide.
            var width = i == posters.Count - 1 ? CollageWidth - sliceWidth * (posters.Count - 1) : sliceWidth;
            filter.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"[{i}]scale=-2:{CollageHeight},crop={width}:{CollageHeight}[s{i}];"
            );
            inputs.Append(System.Globalization.CultureInfo.InvariantCulture, $"[s{i}]");
        }

        filter.Append(inputs).Append(System.Globalization.CultureInfo.InvariantCulture, $"hstack=inputs={posters.Count}");
        if (posters.Count == 1)
        {
            filter.Clear().Append(System.Globalization.CultureInfo.InvariantCulture, $"[0]scale=-2:{CollageHeight},crop={CollageWidth}:{CollageHeight}");
        }

        var startInfo = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        foreach (var poster in posters)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(poster);
        }

        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(filter.ToString());
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add(output);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            logger.LogWarning("ffmpeg took too long tiling the poster collage; using the fallback builder");
            return false;
        }

        await stdout.ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "ffmpeg could not tile the poster collage (exit {ExitCode}): {Error}; using the fallback builder",
                process.ExitCode,
                errors.Trim()
            );
            return false;
        }

        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private ArtworkState? EmbeddedCoverState =>
        LoadEmbeddedCover() is { } cover
            ? new ArtworkState(Convert.ToHexString(SHA256.HashData(cover)), 0, DateTimeOffset.MinValue)
            : null;

    private byte[]? LoadEmbeddedCover()
    {
        const string resourceName = "NebulaBridge.Assets.nebula-library-cover.png";
        using var image = typeof(BridgeLibraryService).Assembly.GetManifestResourceStream(resourceName);
        if (image is null)
        {
            logger.LogWarning("Managed-library artwork resource {ResourceName} is missing", resourceName);
            return null;
        }

        using var buffer = new MemoryStream();
        image.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// What the plugin last wrote for a library. <paramref name="Layout"/> names the collage
    /// style so a build that changes it re-tiles instead of waiting for the refresh interval.
    /// </summary>
    internal sealed record ArtworkState(string Sha256, int PosterCount, DateTimeOffset WrittenAt, int Layout = 0);

    private string ArtworkStatePath(string key) => Path.Combine(ArtworkPath, key + ".json");

    private ArtworkState? ReadArtworkState(string key)
    {
        try
        {
            var path = ArtworkStatePath(key);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ArtworkState>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteArtworkState(string key, ArtworkState state)
    {
        try
        {
            Directory.CreateDirectory(ArtworkPath);
            File.WriteAllText(ArtworkStatePath(key), JsonSerializer.Serialize(state));
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not record artwork state for {Key}", key);
        }
    }

    /// <summary>The current poster is ours when its bytes match what we last wrote.</summary>
    internal static bool IsOwnedArtwork(string? path, ArtworkState? state)
    {
        if (state is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            using var stream = File.OpenRead(path);
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                state.Sha256,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static bool IsLegacyBlankArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Jellyfin 10.11 files the library image as poster.png; Jellyfin 12 as folder.png.
        var fileName = Path.GetFileName(path);
        if (
            !fileName.Equals("poster.png", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("folder.png", StringComparison.OrdinalIgnoreCase)
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
        var configuration = NebulaBridgePlugin.Instance?.Configuration;
        var virtualPaths = new[]
            {
                configuration?.MoviePath,
                configuration?.SeriesPath,
            }
            .Concat(configuration?.UserConfigs.SelectMany(user => new[] { user.MoviePath, user.SeriesPath }) ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.Ordinal);
        return libraryManager
            .GetVirtualFolders()
            .Where(folder => folder.Locations.Any(location => IsManagedLocation(location, root, virtualPaths)))
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }

    public IReadOnlyList<(Guid Id, string Name)> GetVirtualFolderSummaries() => libraryManager
        .GetVirtualFolders()
        .Select(folder => (Id: Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty, Name: folder.Name ?? string.Empty))
        .Where(entry => entry.Id != Guid.Empty)
        .DistinctBy(entry => entry.Id)
        .ToList();

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

    public static bool IsNextEpisodes(CatalogConfig catalog) =>
        string.Equals(catalog.Id, TraktNextEpisodesService.CatalogId, StringComparison.Ordinal)
        || catalog.Id.Contains("next", StringComparison.OrdinalIgnoreCase);

    public static bool IsSeries(CatalogConfig catalog) =>
        catalog.Type.Equals("series", StringComparison.OrdinalIgnoreCase);

    /// <summary>Filesystem-safe key for a source that owns its own library.</summary>
    public static string GetCatalogSlug(CatalogConfig catalog)
    {
        var raw = $"{catalog.Source}-{catalog.Id}".ToLowerInvariant();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    public static CatalogRefreshCadence GetCadence(CatalogConfig catalog)
    {
        if (IsNextEpisodes(catalog))
            return CatalogRefreshCadence.TwiceDaily;

        var id = catalog.Id.ToLowerInvariant();
        return id.Contains("trending", StringComparison.Ordinal)
            || id.Contains("box-office", StringComparison.Ordinal)
            || id.Contains("boxoffice", StringComparison.Ordinal)
            ? CatalogRefreshCadence.Daily
            : CatalogRefreshCadence.Weekly;
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

    internal static bool IsManagedLocation(
        string location,
        string managedRoot,
        IReadOnlySet<string> configuredVirtualPaths
    ) => IsUnderRoot(location, managedRoot)
        || configuredVirtualPaths.Contains(Path.GetFullPath(location));
}

public enum CatalogRefreshCadence
{
    TwiceDaily,
    Daily,
    Weekly,
}
