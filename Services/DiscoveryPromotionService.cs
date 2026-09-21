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
    BackgroundWork backgroundWork,
    ILogger<DiscoveryPromotionService> logger
) : IHostedService
{
    private readonly KeyLock _promotionLock = new();

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
        if (!eventArgs.UserData.HasMeaningfulInteraction())
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

        backgroundWork.Run(
            "discovery-promotion",
            ct => _promotionLock.RunSingleFlightAsync(item.Id, token => PromoteCoreAsync(item, eventArgs.UserId, token), ct),
            item.Id);
    }

    private async Task PromoteCoreAsync(BaseItem item, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = bridgeLibraries.GetAggregateDescriptor(item is Series);
            var destination = await bridgeLibraries
                .EnsureLibraryAsync(descriptor, cancellationToken)
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
                .PromoteDiscoveryItemAsync(item, userId, cancellationToken, destination, descriptor.Key)
                .ConfigureAwait(false);
            await userAccess.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
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
