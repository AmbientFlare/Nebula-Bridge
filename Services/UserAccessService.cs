using Jellyfin.Data.Events;
using MediaBrowser.Controller.Library;
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
    ILogger<UserAccessService> logger
) : IHostedService
{
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        userManager.OnUserUpdated += OnUserUpdated;
        NebulaBridgePlugin.ConfigurationChanged += OnConfigurationChanged;
        _ = ReconcileAllSafelyAsync();
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
                else if (access?.LibraryPolicyCaptured == true)
                {
                    policy.EnableAllFolders = access.PreviousEnableAllFolders;
                    policy.EnabledFolders = [.. access.PreviousEnabledFolderIds];
                    await userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
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

    private void OnUserUpdated(object? sender, GenericEventArgs<Jellyfin.Database.Implementations.Entities.User> args) =>
        _ = ReconcileAllSafelyAsync();

    private void OnConfigurationChanged(PluginConfiguration configuration) =>
        _ = ReconcileAllSafelyAsync();

    private async Task ReconcileAllSafelyAsync()
    {
        try
        {
            await ReconcileAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not reconcile Nebula Bridge user access policies");
        }
    }

    private static bool IsDefaultConfig(UserConfig config) =>
        !config.NoNebulaBridge
        && !config.DisableSearch
        && string.IsNullOrWhiteSpace(config.Notes)
        && string.IsNullOrWhiteSpace(config.Url)
        && string.IsNullOrWhiteSpace(config.MoviePath)
        && string.IsNullOrWhiteSpace(config.SeriesPath)
        && !config.LibraryPolicyCaptured;
}
