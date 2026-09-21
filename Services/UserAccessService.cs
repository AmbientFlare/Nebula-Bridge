using Jellyfin.Data.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

public sealed record NebulaBridgeUserAccess(
    Guid UserId,
    string UserName,
    bool IsDisabled,
    bool NoNebulaBridge,
    bool LocalSearchOnly,
    string Notes
);

/// <summary>
/// Applies Nebula Bridge visibility using Jellyfin's native library policy and keeps the
/// plugin's per-user discovery gate synchronized with Jellyfin users.
/// </summary>
public sealed class UserAccessService(
    IUserManager userManager,
    BridgeLibraryService libraries,
    BackgroundWork backgroundWork,
    ILogger<UserAccessService> logger
) : IHostedService
{
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        userManager.OnUserUpdated += OnUserUpdated;
        NebulaBridgePlugin.ConfigurationChanged += OnConfigurationChanged;
        ReconcileInBackground();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userManager.OnUserUpdated -= OnUserUpdated;
        NebulaBridgePlugin.ConfigurationChanged -= OnConfigurationChanged;
        return Task.CompletedTask;
    }

    public IReadOnlyList<NebulaBridgeUserAccess> GetRows()
    {
        var cfg = NebulaBridgePlugin.Instance!.Configuration;
        return userManager
            .GetUsers()
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .Select(user =>
            {
                var policy = userManager.GetUserDto(user).Policy;
                var access = cfg.UserConfigs.FirstOrDefault(item => item.UserId == user.Id);
                return new NebulaBridgeUserAccess(
                    user.Id,
                    user.Username,
                    policy?.IsDisabled == true,
                    access?.NoNebulaBridge == true,
                    access?.DisableSearch == true,
                    access?.Notes ?? string.Empty
                );
            })
            .ToList();
    }

    public async Task SaveRowsAsync(
        IReadOnlyList<NebulaBridgeUserAccess> rows,
        CancellationToken cancellationToken
    )
    {
        var cfg = NebulaBridgePlugin.Instance!.Configuration;
        var currentUsers = userManager.GetUsers().Select(user => user.Id).ToHashSet();
        var requested = rows
            .Where(row => currentUsers.Contains(row.UserId))
            .ToDictionary(row => row.UserId);

        foreach (var userId in currentUsers)
        {
            var existing = cfg.UserConfigs.FirstOrDefault(item => item.UserId == userId);
            if (!requested.TryGetValue(userId, out var row))
            {
                continue;
            }

            existing ??= new UserConfig { UserId = userId };
            if (!cfg.UserConfigs.Contains(existing))
            {
                cfg.UserConfigs.Add(existing);
            }

            existing.NoNebulaBridge = row.NoNebulaBridge;
            existing.DisableSearch = row.LocalSearchOnly;
            existing.Notes = row.Notes?.Trim() ?? string.Empty;
        }

        cfg.UserConfigs.RemoveAll(item => !currentUsers.Contains(item.UserId));
        NebulaBridgePlugin.Instance.SaveConfiguration();
        NebulaBridgePlugin.Instance.InvalidateRuntimeConfiguration();
        await ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
        if (cfg.UserConfigs.RemoveAll(IsDefaultConfig) > 0)
        {
            NebulaBridgePlugin.Instance.SaveConfiguration();
            NebulaBridgePlugin.Instance.InvalidateRuntimeConfiguration();
        }
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cfg = NebulaBridgePlugin.Instance!.Configuration;
            var managedIds = libraries.GetManagedVirtualFolderIds();
            var allFolderIds = libraries.GetVisibleVirtualFolderIds().ToArray();
            var configChanged = false;
            foreach (var user in userManager.GetUsers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var access = cfg.UserConfigs.FirstOrDefault(item => item.UserId == user.Id);
                var policy = userManager.GetUserDto(user).Policy ?? new UserPolicy();
                if (access?.NoNebulaBridge == true)
                {
                    if (!access.LibraryPolicyCaptured)
                    {
                        access.LibraryPolicyCaptured = true;
                        access.PreviousEnableAllFolders = policy.EnableAllFolders;
                        access.PreviousEnabledFolderIds = [.. (policy.EnabledFolders ?? [])];
                        configChanged = true;
                    }

                    var allowed = access.PreviousEnableAllFolders
                        ? allFolderIds.Where(id => !managedIds.Contains(id)).ToArray()
                        : access.PreviousEnabledFolderIds
                            .Where(id => !managedIds.Contains(id))
                            .ToArray();
                    if (
                        policy.EnableAllFolders
                        || !(policy.EnabledFolders ?? []).SequenceEqual(allowed)
                    )
                    {
                        policy.EnableAllFolders = false;
                        policy.EnabledFolders = allowed;
                        await userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                    }
                }
                else
                {
                    await EnsureManagedViewsLastAsync(user, managedIds).ConfigureAwait(false);
                }

                if (access?.NoNebulaBridge != true && access?.LibraryPolicyCaptured == true)
                {
                    // Restore only when the policy still contains the restriction we applied.
                    // If an administrator edited it while the restriction was active, preserve
                    // that edit rather than overwriting it with a stale snapshot.
                    var allowed = access.PreviousEnableAllFolders
                        ? allFolderIds.Where(id => !managedIds.Contains(id)).ToArray()
                        : access.PreviousEnabledFolderIds
                            .Where(id => !managedIds.Contains(id))
                            .ToArray();
                    if (policy.EnableAllFolders == false && (policy.EnabledFolders ?? []).SequenceEqual(allowed))
                    {
                        policy.EnableAllFolders = access.PreviousEnableAllFolders;
                        policy.EnabledFolders = [.. access.PreviousEnabledFolderIds];
                        await userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                    }
                    access.LibraryPolicyCaptured = false;
                    access.PreviousEnableAllFolders = false;
                    access.PreviousEnabledFolderIds = [];
                    configChanged = true;
                }
            }

            if (configChanged)
            {
                NebulaBridgePlugin.Instance.SaveConfiguration();
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    /// <summary>
    /// Keeps every Nebula Bridge library behind the server's own Movies/Shows/Music views on
    /// the home screen. A user's existing order is respected; only views they have never
    /// placed are appended, native ones first.
    /// </summary>
    /// <summary>
    /// Returns the home-screen view order with every Nebula Bridge view after the server's own
    /// views, or null when the current order already satisfies that. The relative order a user
    /// chose inside each group is preserved; views that no longer exist are dropped and new ones
    /// are appended to their group in name order.
    /// </summary>
    internal static IReadOnlyList<Guid>? BuildViewOrder(
        IReadOnlyList<Guid> current,
        IReadOnlyList<(Guid Id, string Name)> folders,
        IReadOnlySet<Guid> managedIds
    )
    {
        var known = folders.Select(folder => folder.Id).ToHashSet();
        var placed = current.Where(known.Contains).Distinct().ToList();
        var placedSet = placed.ToHashSet();
        var unplaced = folders
            .Where(folder => !placedSet.Contains(folder.Id))
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(folder => folder.Id)
            .ToList();
        var next = new List<Guid>();
        next.AddRange(placed.Where(id => !managedIds.Contains(id)));
        next.AddRange(unplaced.Where(id => !managedIds.Contains(id)));
        next.AddRange(placed.Where(managedIds.Contains));
        next.AddRange(unplaced.Where(managedIds.Contains));
        return next.SequenceEqual(current) ? null : next;
    }

    private async Task EnsureManagedViewsLastAsync(
        Jellyfin.Database.Implementations.Entities.User user,
        IReadOnlySet<Guid> managedIds
    )
    {
        var configuration = userManager.GetUserDto(user).Configuration ?? new UserConfiguration();
        var current = (configuration.OrderedViews ?? []).Where(id => id != Guid.Empty).ToList();
        var next = BuildViewOrder(current, libraries.GetVirtualFolderSummaries(), managedIds);
        if (next is null)
        {
            return;
        }

        configuration.OrderedViews = [.. next];
        await userManager.UpdateConfigurationAsync(user.Id, configuration).ConfigureAwait(false);
        logger.LogDebug("Placed Nebula Bridge libraries after native views for {User}", user.Username);
    }

    private void OnUserUpdated(object? sender, GenericEventArgs<Jellyfin.Database.Implementations.Entities.User> args) =>
        ReconcileInBackground();

    private void OnConfigurationChanged(PluginConfiguration configuration) =>
        ReconcileInBackground();

    private void ReconcileInBackground() =>
        backgroundWork.Run("user-access-reconcile", ReconcileAllAsync);

    private static bool IsDefaultConfig(UserConfig config) =>
        !config.NoNebulaBridge
        && !config.DisableSearch
        && string.IsNullOrWhiteSpace(config.Notes)
        && string.IsNullOrWhiteSpace(config.Url)
        && string.IsNullOrWhiteSpace(config.MoviePath)
        && string.IsNullOrWhiteSpace(config.SeriesPath)
        && !config.LibraryPolicyCaptured;
}
