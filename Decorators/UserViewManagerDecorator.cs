using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using NebulaBridge.Services;

namespace NebulaBridge.Decorators;

/// <summary>
/// Keeps the Nebula Bridge libraries out of the "Latest" rows. Jellyfin orders those rows by
/// when an item was added, and Nebula Bridge adds items in bulk (whole seasons, search results),
/// so they would otherwise crowd out the server's own media. Bridge items still reach the home
/// screen through Continue Watching and Next Up. A user with nothing of their own for a row
/// still gets the Bridge items, so the row is never left empty.
/// </summary>
public sealed class UserViewManagerDecorator(
    IUserViewManager inner,
    ILibraryManager libraryManager,
    Lazy<BridgeLibraryService> libraries
) : IUserViewManager
{
    // Jellyfin's own default when the caller gives no limit.
    private const int DefaultLimit = 10;

    public Folder[] GetUserViews(UserViewQuery query) => inner.GetUserViews(query);

    public UserView GetUserSubView(
        Guid parentId,
        CollectionType? type,
        string localizationKey,
        string sortName
    ) => inner.GetUserSubView(parentId, type, localizationKey, sortName);

    public List<Tuple<BaseItem, List<BaseItem>>> GetLatestItems(
        LatestItemsQuery request,
        DtoOptions options
    )
    {
        // A request for one library, Bridge or not, is answered as asked.
        if (
            NebulaBridgePlugin.Instance?.Configuration.HideFromLatest != true
            || request.ParentId != Guid.Empty
            || request.User is null
        )
        {
            return inner.GetLatestItems(request, options);
        }

        var managed = libraries.Value.GetManagedVirtualFolderIds();
        var excluded = request.User.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
        var folders = libraryManager
            .GetUserRootFolder()
            .GetChildren(request.User, true)
            .Where(item => item is Folder && !excluded.Contains(item.Id))
            .ToList();
        if (!folders.Any(folder => managed.Contains(folder.Id)))
        {
            return inner.GetLatestItems(request, options);
        }

        // Jellyfin can only scope this query to one library, so ask each of the user's own
        // libraries in turn and merge, newest first.
        var perLibrary = folders
            .Where(folder => !managed.Contains(folder.Id))
            .Select(folder => inner.GetLatestItems(ForLibrary(request, folder.Id), options));
        var merged = Merge(perLibrary, request.IncludeItemTypes, request.Limit ?? DefaultLimit);
        return merged.Count > 0 ? merged : inner.GetLatestItems(request, options);
    }

    /// <summary>
    /// Scoped to one library, Jellyfin answers with that library's own kind of item (albums for
    /// music) whatever types were asked for, so anything that does not belong in the requested
    /// row is dropped here. Shows come back as series or seasons, and albums stand for songs.
    /// </summary>
    internal static List<Tuple<BaseItem, List<BaseItem>>> Merge(
        IEnumerable<List<Tuple<BaseItem, List<BaseItem>>>> perLibrary,
        IReadOnlyCollection<BaseItemKind>? includeItemTypes,
        int limit
    )
    {
        var wanted = includeItemTypes is { Count: > 0 } ? Expand(includeItemTypes) : null;
        return perLibrary
            .SelectMany(groups => groups)
            .Where(group =>
                group.Item2.Count > 0
                && (wanted is null || group.Item2.Any(item => wanted.Contains(item.GetBaseItemKind())))
            )
            .OrderByDescending(group => group.Item2.Max(item => item.DateCreated))
            .Take(Math.Max(0, limit))
            .ToList();
    }

    private static HashSet<BaseItemKind> Expand(IEnumerable<BaseItemKind> kinds)
    {
        var wanted = kinds.ToHashSet();
        if (wanted.Contains(BaseItemKind.Episode))
        {
            wanted.Add(BaseItemKind.Series);
            wanted.Add(BaseItemKind.Season);
        }

        if (wanted.Contains(BaseItemKind.Audio))
        {
            wanted.Add(BaseItemKind.MusicAlbum);
        }

        return wanted;
    }

    private static LatestItemsQuery ForLibrary(LatestItemsQuery request, Guid parentId) =>
        new()
        {
            User = request.User,
            ParentId = parentId,
            StartIndex = request.StartIndex,
            Limit = request.Limit,
            Fields = request.Fields,
            IncludeItemTypes = request.IncludeItemTypes,
            IsPlayed = request.IsPlayed,
            GroupItems = request.GroupItems,
            EnableImages = request.EnableImages,
            ImageTypeLimit = request.ImageTypeLimit,
            EnableImageTypes = request.EnableImageTypes,
        };
}
