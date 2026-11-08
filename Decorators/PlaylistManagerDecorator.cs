using System.Globalization;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Decorators;

public sealed class PlaylistManagerDecorator(
    IPlaylistManager inner,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IProviderManager providerManager,
    IDirectoryService directoryService,
    ILogger<PlaylistManagerDecorator> log
) : IPlaylistManager
{
    public Playlist GetPlaylistForUser(Guid playlistId, Guid userId) =>
        inner.GetPlaylistForUser(playlistId, userId);

    public Task<PlaylistCreationResult> CreatePlaylist(PlaylistCreationRequest request) =>
        inner.CreatePlaylist(request);

    public Task UpdatePlaylist(PlaylistUpdateRequest request) => inner.UpdatePlaylist(request);

    public IEnumerable<Playlist> GetPlaylists(Guid userId) => inner.GetPlaylists(userId);

    public Task AddUserToShares(PlaylistUserUpdateRequest request) =>
        inner.AddUserToShares(request);

    public Task RemoveUserFromShares(Guid playlistId, Guid userId, PlaylistUserPermissions share) =>
        inner.RemoveUserFromShares(playlistId, userId, share);

    // Jellyfin 12 added an int? position parameter so callers can insert rather
    // than only append. The shared core honours it; the 10.11 signature pins it
    // to null, which is that line's only behaviour.
#if JELLYFIN_12
    public Task AddItemToPlaylistAsync(
        Guid playlistId,
        IReadOnlyCollection<Guid> itemIds,
        int? position,
        Guid userId
    ) => AddItemToPlaylistCoreAsync(playlistId, itemIds, position, userId);
#else
    public Task AddItemToPlaylistAsync(
        Guid playlistId,
        IReadOnlyCollection<Guid> itemIds,
        Guid userId
    ) => AddItemToPlaylistCoreAsync(playlistId, itemIds, null, userId);
#endif

    private async Task AddItemToPlaylistCoreAsync(
        Guid playlistId,
        IReadOnlyCollection<Guid> itemIds,
        int? position,
        Guid userId
    )
    {
        if (libraryManager.GetItemById(playlistId) is not Playlist playlist)
            throw new ArgumentException("No Playlist exists with Id " + playlistId);

        var user = userId == Guid.Empty ? null : userManager.GetUserById(userId);
        var options = new DtoOptions(false) { EnableImages = true };

        var resolved = itemIds.Select(libraryManager.GetItemById).Where(i => i is not null);
        var newItems = Playlist
            .GetPlaylistItems(resolved, user, options)
            .Where(i => i.SupportsAddingToPlaylist);

        var existingIds = playlist.LinkedChildren.Select(c => c.ItemId).ToHashSet();
        var toAdd = newItems.Where(i => !existingIds.Contains(i.Id)).Distinct().ToList();

        var numDuplicates = itemIds.Count - toAdd.Count;
        if (numDuplicates > 0)
            log.LogWarning(
                "Ignored adding {DuplicateCount} duplicate items to playlist {PlaylistName}.",
                numDuplicates,
                playlist.Name
            );

        if (toAdd.Count == 0)
            return;

        var newChildren = toAdd.Select(item => item.CreateLinkedChild()).ToArray();

        if (position is null || position.Value >= playlist.LinkedChildren.Length)
            playlist.LinkedChildren = [.. playlist.LinkedChildren, .. newChildren];
        else if (position.Value <= 0)
            playlist.LinkedChildren = [.. newChildren, .. playlist.LinkedChildren];
        else
            playlist.LinkedChildren =
            [
                .. playlist.LinkedChildren[..position.Value],
                .. newChildren,
                .. playlist.LinkedChildren[position.Value..],
            ];
        playlist.DateLastMediaAdded = DateTime.UtcNow;

        await playlist
            .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
            .ConfigureAwait(false);

        if (playlist.IsFile)
            inner.SavePlaylistFile(playlist);

        providerManager.QueueRefresh(
            playlist.Id,
            new MetadataRefreshOptions(directoryService) { ForceSave = true },
            RefreshPriority.High
        );
    }

    public Task RemoveItemFromPlaylistAsync(string playlistId, IEnumerable<string> entryIds) =>
        inner.RemoveItemFromPlaylistAsync(playlistId, entryIds);

    public Folder GetPlaylistsFolder() => inner.GetPlaylistsFolder();

    public Folder GetPlaylistsFolder(Guid userId) => inner.GetPlaylistsFolder(userId);

    public Task MoveItemAsync(
        string playlistId,
        string entryId,
        int newIndex,
        Guid callingUserId
    ) => inner.MoveItemAsync(playlistId, entryId, newIndex, callingUserId);

    public Task RemovePlaylistsAsync(Guid userId) => inner.RemovePlaylistsAsync(userId);

    public void SavePlaylistFile(Playlist item) => inner.SavePlaylistFile(item);
}
