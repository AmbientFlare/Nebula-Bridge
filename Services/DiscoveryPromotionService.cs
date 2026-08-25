using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

/// <summary>
/// Promotes search-discovered items out of quarantine once a user watches or follows them.
/// </summary>
public sealed class DiscoveryPromotionService(
    IUserDataManager userDataManager,
    ILibraryManager libraryManager,
    NebulaBridgeManager manager,
    BridgeLibraryService bridgeLibraries,
    UserAccessService userAccess,
    ILogger<DiscoveryPromotionService> logger
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        if (!eventArgs.UserData.Played && !eventArgs.UserData.IsFavorite)
        {
            return;
        }

        var item = eventArgs.Item;
        if (item is Episode episode)
        {
            item = libraryManager.GetItemById(episode.SeriesId) ?? item;
        }

        if (!item.HasDiscoveryTag())
        {
            return;
        }

        _ = PromoteSafelyAsync(item, eventArgs.UserId);
    }

    private async Task PromoteSafelyAsync(BaseItem item, Guid userId)
    {
        try
        {
            var destination = await bridgeLibraries
                .EnsureLibraryAsync(
                    bridgeLibraries.GetPromotionDescriptor(item is Series),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            if (destination is null)
            {
                logger.LogWarning(
                    "Could not promote discovery item {Name}: the managed destination library is not ready",
                    item.Name
                );
                return;
            }

            await manager
                .PromoteDiscoveryItemAsync(item, userId, CancellationToken.None, destination)
                .ConfigureAwait(false);
            await userAccess.ReconcileAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not promote discovery item {Name} ({ItemId}) after user activity",
                item.Name,
                item.Id
            );
        }
    }
}
