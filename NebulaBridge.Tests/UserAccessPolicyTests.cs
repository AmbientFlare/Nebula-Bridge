using NebulaBridge.Services;

namespace NebulaBridge.Tests;

/// <summary>
/// "No Nebula Bridge" may only take Nebula Bridge libraries away. It once re-applied a stale
/// "all libraries" snapshot on every reconcile, which re-granted an admin-only library to
/// every restricted user each time the server restarted, undoing the administrator's edits.
/// </summary>
public sealed class UserAccessPolicyTests
{
    private static readonly Guid Movies = Guid.NewGuid();
    private static readonly Guid Shows = Guid.NewGuid();
    private static readonly Guid Private = Guid.NewGuid();
    private static readonly Guid BridgeMovies = Guid.NewGuid();
    private static readonly Guid BridgeShows = Guid.NewGuid();
    private static readonly Guid[] All = [Movies, Shows, Private, BridgeMovies, BridgeShows];
    private static readonly HashSet<Guid> Managed = [BridgeMovies, BridgeShows];

    [Fact]
    public void RestrictingAnAllLibrariesUserPinsTodaysNonBridgeLibraries()
    {
        var next = UserAccessService.BuildRestrictedFolders(true, [], All, Managed);
        Assert.Equal([Movies, Shows, Private], next);
    }

    [Fact]
    public void AdminRemovalOfALibrarySticksWhileRestricted()
    {
        // The administrator took the private library away after the restriction was applied.
        Assert.Null(UserAccessService.BuildRestrictedFolders(false, [Movies, Shows], All, Managed));
    }

    [Fact]
    public void LibrariesCreatedLaterAreNeverGrantedToRestrictedUsers()
    {
        Guid[] laterLibraries = [.. All, Guid.NewGuid()];
        Assert.Null(UserAccessService.BuildRestrictedFolders(false, [Movies, Shows], laterLibraries, Managed));
    }

    [Fact]
    public void RestrictionOnlyRemovesBridgeLibraries()
    {
        var next = UserAccessService.BuildRestrictedFolders(false, [Movies, BridgeShows], All, Managed);
        Assert.Equal([Movies], next);
    }

    [Fact]
    public void OrderDifferencesAloneDoNotRewriteThePolicy()
    {
        Assert.Null(UserAccessService.BuildRestrictedFolders(false, [Shows, Movies], All, Managed));
    }

    [Fact]
    public void RestoreGivesBackAllLibrariesWhenNothingWasRemoved()
    {
        var restored = UserAccessService.BuildRestoredPolicy(true, [], false, [Movies, Shows, Private], All, Managed);
        Assert.Equal((true, (IReadOnlyList<Guid>)[]), (restored!.Value.EnableAllFolders, restored.Value.EnabledFolders));
    }

    [Fact]
    public void RestoreKeepsAnAdminRemovalAndOnlyAddsBridgeLibraries()
    {
        var restored = UserAccessService.BuildRestoredPolicy(true, [], false, [Movies, Shows], All, Managed);
        Assert.False(restored!.Value.EnableAllFolders);
        Assert.Equal([Movies, Shows, BridgeMovies, BridgeShows], restored.Value.EnabledFolders);
    }

    [Fact]
    public void RestoreOfAnExplicitListReturnsOnlyItsBridgeLibraries()
    {
        var restored = UserAccessService.BuildRestoredPolicy(false, [Movies, BridgeMovies], false, [Movies], All, Managed);
        Assert.Equal([Movies, BridgeMovies], restored!.Value.EnabledFolders);
    }

    [Fact]
    public void RestoreLeavesAnAllLibrariesPolicyAlone()
    {
        Assert.Null(UserAccessService.BuildRestoredPolicy(false, [Movies], true, [], All, Managed));
    }
}
