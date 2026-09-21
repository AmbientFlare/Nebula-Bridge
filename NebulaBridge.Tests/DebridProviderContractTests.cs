using System.Net;
using NebulaBridge.Config;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

/// <summary>
/// Contracts every real provider implementation must honour: truthful capability
/// declarations, a shared failure vocabulary, and no secret material in diagnostics.
/// </summary>
public sealed class DebridProviderContractTests
{
    [Fact]
    public void TorBoxDeclaresOnlyItsImplementedPlaybackOperations()
    {
        const DebridProviderCapabilities expected =
            DebridProviderCapabilities.CachedAvailability
            | DebridProviderCapabilities.MagnetSubmission
            | DebridProviderCapabilities.FileSelection
            | DebridProviderCapabilities.DirectStreamUrl
            | DebridProviderCapabilities.CompletedFileSource;

        Assert.Equal(expected, new TorBoxStreamResolver(null!, null!, null!, null!).Capabilities);
        Assert.False(expected.HasFlag((DebridProviderCapabilities)(1 << 10)));
    }

    [Fact]
    public void RealDebridNeverClaimsMagnetSubmissionAndFlagsAccountScopedAvailability()
    {
        var capabilities = new RealDebridStreamResolver(null!, null!, null!, null!).Capabilities;

        // instantAvailability is disabled upstream (error 37): the only truthful cache check is the
        // account's own downloaded torrents, and Nebula never submits magnets on Play.
        Assert.False(capabilities.HasFlag(DebridProviderCapabilities.MagnetSubmission));
        Assert.True(capabilities.HasFlag(DebridProviderCapabilities.AccountScopedAvailability));
        Assert.True(capabilities.HasFlag(DebridProviderCapabilities.CachedAvailability));
        Assert.True(capabilities.HasFlag(DebridProviderCapabilities.CompletedFileSource));
    }

    [Fact]
    public void ProviderIdsAreStableLowercaseAndDistinct()
    {
        var ids = new IDebridProvider[]
        {
            new TorBoxStreamResolver(null!, null!, null!, null!),
            new RealDebridStreamResolver(null!, null!, null!, null!),
        }.Select(p => p.Id).ToList();

        Assert.Equal(["torbox", "realdebrid"], ids);
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id));
        Assert.Equal(ids, DebridProviderCredentials.ProviderIds.ToList());
    }

    [Theory]
    [InlineData(429, null, "rate_limited")]
    [InlineData(403, 5, "rate_limited")]
    [InlineData(403, 34, "rate_limited")]
    [InlineData(401, null, "authentication_rejected")]
    [InlineData(403, 8, "authentication_rejected")]
    [InlineData(403, 14, "account_locked")]
    [InlineData(403, 20, "premium_required")]
    [InlineData(403, 36, "premium_required")]
    [InlineData(503, null, "provider_unavailable")]
    [InlineData(403, 25, "provider_unavailable")]
    [InlineData(403, 37, "endpoint_disabled")]
    [InlineData(403, 9, "permission_denied")]
    [InlineData(400, 1, "api_error")]
    public void RealDebridFailureReasonsMapOntoTheSharedVocabulary(int status, int? code, string expected)
    {
        var reason = RealDebridStreamResolver.FailureReason(new RealDebridStreamResolver.RealDebridApiException(status, code));

        Assert.Equal(expected, reason);
        // Every one of these is an account/provider problem, never a "release not cached" answer.
        Assert.NotEqual(DebridProviderHealth.Healthy, DebridProviderHealthTracker.Classify(reason));
    }

    [Fact]
    public void RealDebridLegalBlockIsAboutTheFileNotTheAccount()
    {
        var reason = RealDebridStreamResolver.FailureReason(new RealDebridStreamResolver.RealDebridApiException(451, null));

        Assert.Equal("blocked_content", reason);
        Assert.Equal(DebridProviderHealth.Healthy, DebridProviderHealthTracker.Classify(reason));
    }

    [Fact]
    public void RealDebridTimeoutIsTransientNotAuthFailure()
    {
        var reason = RealDebridStreamResolver.FailureReason(new TaskCanceledException());

        Assert.Equal("timeout", reason);
        Assert.Equal(DebridProviderHealth.TemporarilyUnavailable, DebridProviderHealthTracker.Classify(reason));
    }

    [Fact]
    public void RealDebridApiExceptionMessageCarriesNoResponseBody()
    {
        var exception = new RealDebridStreamResolver.RealDebridApiException(401, 8);

        Assert.DoesNotContain("token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDiagnosticsRedactTokensSignedUrlsAndAuthorizationHeaders()
    {
        var fields = new Dictionary<string, object?>
        {
            ["provider"] = "realdebrid",
            ["url"] = "https://download.real-debrid.example/d/ABCDEF123/file.mkv?token=sekrit",
            ["header"] = "Authorization: Bearer abcdef0123456789",
            ["apiKey"] = "api_key=abcdef0123456789",
            ["torboxUrl"] = "https://store.torbox.example/dl?token=tb-secret-1",
            ["reason"] = "rate_limited",
        };

        var entry = NebulaBridgeFileLog.FormatEntry(DateTimeOffset.UnixEpoch, NebulaLogVerbosity.Debug, "debrid-cache-check", Guid.Empty, fields);

        Assert.DoesNotContain("sekrit", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef0123456789", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("tb-secret-1", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("download.real-debrid.example", entry, StringComparison.Ordinal);
        Assert.Contains("rate_limited", entry, StringComparison.Ordinal);
        Assert.Contains("realdebrid", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialSlotsAreWriteOnlyAndNamespacedByProviderId()
    {
        var cfg = new PluginConfiguration();

        Assert.True(DebridProviderCredentials.TryWrite(cfg, "realdebrid", "rd-secret"));
        Assert.True(DebridProviderCredentials.TryWrite(cfg, "torbox", "tb-secret"));
        Assert.False(DebridProviderCredentials.TryWrite(cfg, "premiumize", "nope"));

        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("rd-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tb-secret", json, StringComparison.Ordinal);
        Assert.True(DebridProviderCredentials.HasCredential(cfg, "realdebrid"));
        Assert.True(DebridProviderCredentials.HasCredential(cfg, "torbox"));
        Assert.False(DebridProviderCredentials.HasCredential(cfg, "premiumize"));
    }

    /// <summary>
    /// Real-Debrid marks a YIFY release's cover .jpg as selected but generates a link only for the
    /// .mp4, so links (1) never line up with selected files (2). The unrestricted link's own
    /// filename/length must decide rather than the positional index.
    /// </summary>
    [Theory]
    [InlineData("Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4", 1_555_220_705L, true)]
    [InlineData("WWW.YIFY-TORRENTS.COM.jpg", 130_677L, false)]
    [InlineData("Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4", 1_555_220_704L, false)]
    [InlineData("", 0L, true)]
    [InlineData(null, 1_555_220_705L, true)]
    public void RealDebridUnrestrictedLinkIsAcceptedOnlyWhenItDescribesTheChosenFile(string? filename, long filesize, bool expected)
    {
        var link = new RealDebridStreamResolver.RealDebridUnrestrictedLink { Download = "https://x/y", Filename = filename, Filesize = filesize };

        Assert.Equal(expected, RealDebridStreamResolver.DescribesFile(link, "Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4", 1_555_220_705));
    }

    [Theory]
    [InlineData("Grown Ups 2 (2013) [1080p].rar", "application/x-rar-compressed", true)]
    [InlineData("bundle.zip", null, true)]
    [InlineData("bundle", "application/zip", true)]
    [InlineData("Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4", "video/mp4", false)]
    [InlineData(null, null, false)]
    public void RealDebridArchiveBundlesAreRecognised(string? filename, string? mimeType, bool expected)
    {
        var link = new RealDebridStreamResolver.RealDebridUnrestrictedLink { Download = "https://x/y", Filename = filename, MimeType = mimeType };

        Assert.Equal(expected, RealDebridStreamResolver.IsArchive(link));
    }

    [Theory]
    [InlineData("not_cached")]
    [InlineData("links_mismatch")]
    [InlineData("length_mismatch")]
    [InlineData("archive_link")]
    [InlineData("blocked_content")]
    public void TorrentScopedFailuresDoNotDegradeProviderHealth(string reason)
    {
        // Hosted RD run: a torrent stored as a RAR bundle benched the whole account. The provider
        // answered correctly about that one torrent, so the next release must still route to it.
        var tracker = new DebridProviderHealthTracker(() => DateTimeOffset.UnixEpoch);

        Assert.Equal(DebridProviderHealth.Healthy, tracker.ReportFailure("realdebrid", reason));
        Assert.True(tracker.IsAvailable("realdebrid"));
    }

    [Fact]
    public void CompletedFileHandleIdentityIsProviderNeutral()
    {
        var torbox = new DebridCompletedFileHandle("torbox", "1", "9", "a.mkv", 100, "0123456789abcdef0123456789abcdef01234567", "Show/a.mkv");
        var realdebrid = new DebridCompletedFileHandle("realdebrid", "X", "3", "a.mkv", 100, "0123456789ABCDEF0123456789ABCDEF01234567", "Show\\a.mkv");

        Assert.NotNull(torbox.ContentIdentity);
        Assert.Equal(torbox.ContentIdentity!.Key, realdebrid.ContentIdentity!.Key);
        Assert.NotEqual(torbox.ContentIdentity.Key, (torbox with { ExpectedLength = 101 }).ContentIdentity!.Key);
    }
}
