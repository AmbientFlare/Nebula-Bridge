using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.Config;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

/// <summary>
/// Stage 14 contract tests: the same orchestration rules must hold for any pair of providers,
/// so these run against scripted fakes rather than TorBox or Real-Debrid specifics.
/// </summary>
public sealed class MultiProviderOrchestrationTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef01234567";
    private const string HashB = "89abcdef0123456789abcdef0123456789abcdef";

    private static readonly DebridFile File1 = new(1, "Movie.2026/Movie.2026.mkv", 1_000);
    private static readonly DebridFile File1Renamed = new(7, "Other/Movie.2026.mkv", 1_000);
    private static readonly DebridFile File1DifferentLength = new(1, "Movie.2026/Movie.2026.mkv", 1_001);

    [Fact]
    public async Task SingleProviderStillWorksWithoutAnyOtherRegistered()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a]);

        var lookup = await orchestrator.CheckAvailabilityAsync([HashA], default);
        var route = orchestrator.SelectRoute(Candidate(HashA, lookup));

        Assert.Same(a, route!.Primary);
        Assert.Empty(route.Alternates);
    }

    [Fact]
    public async Task PriorityChoosesTransportWhenBothProvidersHoldTheRelease()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);

        var preferA = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));
        var preferB = Orchestrate([a, b], new Row("a", 2), new Row("b", 1));

        var routeA = preferA.SelectRoute(Candidate(HashA, await preferA.CheckAvailabilityAsync([HashA], default)));
        var routeB = preferB.SelectRoute(Candidate(HashA, await preferB.CheckAvailabilityAsync([HashA], default)));

        Assert.Equal("a", routeA!.Primary.Id);
        Assert.Equal(["b"], routeA.Alternates.Select(p => p.Id));
        Assert.Equal("b", routeB!.Primary.Id);
        Assert.Equal(["a"], routeB.Alternates.Select(p => p.Id));
    }

    [Fact]
    public async Task ReleaseCachedOnOnlyOneProviderRoutesThereRegardlessOfPriority()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashB, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));

        var lookup = await orchestrator.CheckAvailabilityAsync([HashA, HashB], default);

        Assert.Equal("a", orchestrator.SelectRoute(Candidate(HashA, lookup))!.Primary.Id);
        Assert.Equal("b", orchestrator.SelectRoute(Candidate(HashB, lookup))!.Primary.Id);
    }

    [Fact]
    public async Task OneProviderFailingDoesNotSuppressTheOthersSources()
    {
        var failing = new ScriptedProvider("a", failure: new NativeSourceFailure("a", "down", Reason: "provider_unavailable"));
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([failing, b], new Row("a", 1), new Row("b", 2));

        var lookup = await orchestrator.CheckAvailabilityAsync([HashA], default);
        var rows = lookup.For(HashA);

        Assert.Contains(rows, r => r.Provider == "a" && r.Status == DebridAvailabilityStatus.ProviderError);
        Assert.Contains(rows, r => r.Provider == "b" && r.Status == DebridAvailabilityStatus.Cached);
        Assert.Equal("b", orchestrator.SelectRoute(Candidate(HashA, lookup))!.Primary.Id);
        Assert.Equal(DebridProviderHealth.TemporarilyUnavailable, orchestrator.Health.Get("a").Health);
    }

    [Fact]
    public async Task RateLimitedProviderIsReportedAndSkippedUntilBackoffExpires()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var limited = new ScriptedProvider("a", failure: new NativeSourceFailure("a", "slow down", Reason: "rate_limited"));
        var health = new DebridProviderHealthTracker(() => now);
        var orchestrator = new DebridProviderOrchestrator(
            [limited],
            Settings(("a", 1)),
            health,
            () => now,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero);

        var first = await orchestrator.CheckAvailabilityAsync([HashA], default);
        var second = await orchestrator.CheckAvailabilityAsync([HashA], default);

        Assert.Equal(DebridAvailabilityStatus.RateLimited, first.For(HashA).Single().Status);
        Assert.Equal(DebridAvailabilityStatus.RateLimited, second.For(HashA).Single().Status);
        Assert.Equal(1, limited.CacheChecks);
        Assert.Equal(DebridProviderHealth.RateLimited, health.Get("a").Health);

        now = now.AddMinutes(2);
        await orchestrator.CheckAvailabilityAsync([HashA], default);
        Assert.Equal(2, limited.CacheChecks);
    }

    [Fact]
    public async Task ThrowingProviderIsMarkedUnavailableWithoutFailingTheLookup()
    {
        var throwing = new ScriptedProvider("a", exception: new HttpRequestException("boom"));
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([throwing, b]);

        var lookup = await orchestrator.CheckAvailabilityAsync([HashA], default);

        Assert.Equal(DebridAvailabilityStatus.ProviderError, lookup.For(HashA).Single(r => r.Provider == "a").Status);
        Assert.NotNull(orchestrator.SelectRoute(Candidate(HashA, lookup)));
        Assert.False(orchestrator.Health.IsAvailable("a"));
    }

    [Fact]
    public async Task DisabledProviderIsNeverCalled()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1, false), new Row("b", 2, true));

        var lookup = await orchestrator.CheckAvailabilityAsync([HashA], default);

        Assert.Equal(0, a.CacheChecks);
        Assert.Equal(1, b.CacheChecks);
        Assert.Equal("b", orchestrator.SelectRoute(Candidate(HashA, lookup))!.Primary.Id);
        Assert.Equal(["b"], orchestrator.GetActiveProviders().Select(p => p.Id));
    }

    [Fact]
    public async Task ProviderWithoutCredentialsIsInactiveAndNeverCalled()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a], new Row("a", 1, true, ""));

        var lookup = await orchestrator.CheckAvailabilityAsync([HashA], default);

        Assert.Equal(0, a.CacheChecks);
        Assert.Empty(lookup.For(HashA));
        var description = orchestrator.DescribeProviders().Single();
        Assert.False(description.HasCredential);
        Assert.False(description.Active);
    }

    [Fact]
    public async Task PipelineCollapsesOneReleaseIntoOneStreamWithProviderOnlyInDiagnostics()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));
        var pipeline = new NativeSourcePipeline(null!, null!, [], orchestrator, NullLogger<NativeSourcePipeline>.Instance);

        var result = await pipeline.AttachDebridAvailabilityAsync(
            new NativeSearchResult([Candidate(HashA)], []),
            default);
        var prepared = pipeline.PrepareDebridStream(result.Candidates.Single(), new NativeMediaQuery("Movie", 2026));

        Assert.Single(result.Candidates);
        Assert.NotNull(prepared);
        Assert.Equal("debrid:indexer", prepared!.SourceId);
        Assert.DoesNotContain("a", prepared.Name.Split(' '));
        Assert.Equal("a", prepared.DebridRequest!.Provider);
        Assert.Equal(["b"], prepared.DebridRequest.Alternates!);
        Assert.Equal(File1.Name, prepared.DebridRequest.PinnedFile!.FilePath);
    }

    [Fact]
    public async Task AlternateThatHoldsADifferentFileIsNotRetained()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1DifferentLength)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));
        var pipeline = new NativeSourcePipeline(null!, null!, [], orchestrator, NullLogger<NativeSourcePipeline>.Instance);

        var result = await pipeline.AttachDebridAvailabilityAsync(new NativeSearchResult([Candidate(HashA)], []), default);
        var prepared = pipeline.PrepareDebridStream(result.Candidates.Single(), new NativeMediaQuery("Movie", 2026));

        Assert.Empty(prepared!.DebridRequest!.Alternates!);
    }

    /// <summary>
    /// Observed on the hosted Jellyfin 10.11 lab (2026-09-20): TorBox reports
    /// "Grown Ups 2 (2013) [1080p]/Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4" and Real-Debrid
    /// "/Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4" for the same 1,555,220,705-byte file. The
    /// alternate was dropped and a Real-Debrid failure had nowhere to fail over to.
    /// </summary>
    [Fact]
    public async Task AlternateIsRetainedWhenProvidersDisagreeOnlyAboutTheRootFolder()
    {
        var rootFolderKept = new DebridFile(0, "Grown Ups 2 (2013) [1080p]/Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4", 1_555_220_705);
        var rootFolderStripped = new DebridFile(1, "/Grown.Ups.2.2013.1080p.BluRay.x264.YIFY.mp4", 1_555_220_705);
        var rd = new ScriptedProvider("realdebrid", cached: [(HashA, rootFolderStripped)], playbackFailure: new NativeSourceFailure("realdebrid", "no link", Reason: "links_mismatch"));
        var torbox = new ScriptedProvider("torbox", cached: [(HashA, rootFolderKept)]);
        var orchestrator = Orchestrate([rd, torbox], new Row("realdebrid", 1), new Row("torbox", 100));
        var pipeline = new NativeSourcePipeline(null!, null!, [], orchestrator, NullLogger<NativeSourcePipeline>.Instance);

        var result = await pipeline.AttachDebridAvailabilityAsync(new NativeSearchResult([Candidate(HashA)], []), default);
        var prepared = pipeline.PrepareDebridStream(result.Candidates.Single(), new NativeMediaQuery("Grown Ups 2", 2013));

        Assert.Equal("realdebrid", prepared!.DebridRequest!.Provider);
        Assert.Equal(["torbox"], prepared.DebridRequest.Alternates!);

        var playback = await orchestrator.ResolvePlaybackAsync(prepared.DebridRequest, default);

        Assert.Equal("torbox", playback.Provider!.Id);
        Assert.Equal(["realdebrid:failed", "torbox:resolved"], playback.Attempts.Select(x => $"{x.ProviderId}:{x.Outcome}"));
        Assert.Equal(rootFolderKept.SizeBytes, playback.Stream!.CompletedFileHandle!.ExpectedLength);
    }

    [Theory]
    [InlineData("Movie (2013)/Movie.mp4", "/Movie.mp4", true)]
    [InlineData("Movie (2013)/Movie.mp4", "Movie (2013)/Movie.mp4", true)]
    [InlineData("Pack/Disc1/VIDEO_TS/VTS_01_1.VOB", "Disc1/VIDEO_TS/VTS_01_1.VOB", true)]
    [InlineData("Pack/Disc1/VIDEO_TS/VTS_01_1.VOB", "Disc2/VIDEO_TS/VTS_01_1.VOB", false)]
    [InlineData("Movie (2013)/Movie.mp4", "e.mp4", false)]
    [InlineData("Movie (2013)/Movie.mp4", "Other.mp4", false)]
    public void IdentityMatchesAcrossRootFolderConventionsButNotAcrossFiles(string a, string b, bool expected)
    {
        var left = DebridFileIdentity.TryCreate(HashA, a, 10)!;
        var right = DebridFileIdentity.TryCreate(HashA, b, 10)!;

        Assert.Equal(expected, left.Matches(right));
        Assert.Equal(expected, right.Matches(left));
        Assert.False(left.Matches(right with { Length = 11 }));
        Assert.False(left.Matches(right with { InfoHash = HashB }));
    }

    [Fact]
    public void JobIdentityIsTheSameWhicheverProviderReportedTheRootFolder()
    {
        var itemId = Guid.NewGuid();
        var viaTorbox = new DebridCompletedFileHandle("torbox", "1", "0", "Movie.mp4", 1_000, HashA, "Movie (2013)/Movie.mp4");
        var viaRd = new DebridCompletedFileHandle("realdebrid", "X", "1", "Movie.mp4", 1_000, HashA, "/Movie.mp4");
        var fullPathEraJob = Job(itemId, viaTorbox, AcquisitionJobIdentity.CreateFullPathScoped(itemId, viaTorbox)!);

        Assert.Equal(AcquisitionJobIdentity.Create(itemId, viaTorbox), AcquisitionJobIdentity.Create(itemId, viaRd));
        // Found under its stored key by the provider that wrote it...
        Assert.True(AcquisitionJobIdentity.MatchesAny(fullPathEraJob, AcquisitionJobIdentity.Candidates(itemId, viaTorbox)));
        // ...and, once loaded (which upgrades the key), by the other provider too.
        var loaded = AcquisitionJobIdentity.UpgradeStoredIdentity(fullPathEraJob);
        Assert.Equal(AcquisitionJobIdentity.Create(itemId, viaTorbox), loaded.CacheIdentity);
        Assert.True(AcquisitionJobIdentity.MatchesAny(loaded, AcquisitionJobIdentity.Candidates(itemId, viaRd)));
        Assert.Same(loaded, AcquisitionJobIdentity.UpgradeStoredIdentity(loaded));
    }

    [Fact]
    public async Task PreJobFailoverKeepsTheExactFileWhenThePreferredProviderFailsSourceGeneration()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)], playbackFailure: new NativeSourceFailure("a", "no url", Reason: "api_error"));
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));
        var pinned = DebridFileIdentity.TryCreate(HashA, File1.Name, File1.SizeBytes!.Value);

        var playback = await orchestrator.ResolvePlaybackAsync(
            new DebridPlaybackRequest("a", Candidate(HashA), new NativeMediaQuery("Movie"), ["b"], pinned),
            default);

        Assert.Equal("b", playback.Provider!.Id);
        Assert.Equal(pinned!.Key, playback.Stream!.CompletedFileHandle!.ContentIdentity!.Key);
        Assert.Equal(["a:failed", "b:resolved"], playback.Attempts.Select(x => $"{x.ProviderId}:{x.Outcome}"));
    }

    [Fact]
    public async Task FailoverRefusesAnAlternateThatWouldServeDifferentBytes()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)], playbackFailure: new NativeSourceFailure("a", "no url", Reason: "api_error"));
        var b = new ScriptedProvider("b", cached: [(HashA, File1DifferentLength)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));
        var pinned = DebridFileIdentity.TryCreate(HashA, File1.Name, File1.SizeBytes!.Value);

        var playback = await orchestrator.ResolvePlaybackAsync(
            new DebridPlaybackRequest("a", Candidate(HashA), new NativeMediaQuery("Movie"), ["b"], pinned),
            default);

        Assert.Null(playback.Stream);
        Assert.Contains(playback.Attempts, x => x.ProviderId == "b" && x.Reason is "different_file" or "pinned_file_missing");
    }

    [Fact]
    public async Task ProxyRefreshFailsOverToAlternateWithoutChangingTheProxyKeyOrFile()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1), new Row("b", 2));
        var registry = new NativeStreamProxyRegistry(orchestrator);
        var pinned = DebridFileIdentity.TryCreate(HashA, File1.Name, File1.SizeBytes!.Value);
        var proxy = registry.Register(
            new NativePreparedStream(
                "debrid:indexer",
                "Movie 2026",
                File1.SizeBytes,
                "Movie.2026.mkv",
                DebridRequest: new DebridPlaybackRequest("a", Candidate(HashA), new NativeMediaQuery("Movie"), ["b"], pinned)),
            8096);
        var key = proxy.Segments[^1];

        var first = await registry.ResolveTargetAsync(key, false, default);
        a.PlaybackFailure = new NativeSourceFailure("a", "expired", Reason: "provider_unavailable");
        var refreshed = await registry.ResolveTargetAsync(key, true, default);

        Assert.Equal("a", first.Stream!.CompletedFileHandle!.ProviderId);
        Assert.Equal("b", refreshed.Stream!.CompletedFileHandle!.ProviderId);
        Assert.Equal(first.Stream.CompletedFileHandle.ContentIdentity!.Key, refreshed.Stream.CompletedFileHandle.ContentIdentity!.Key);
        Assert.Equal(first.Stream.SizeBytes, refreshed.Stream.SizeBytes);
    }

    [Fact]
    public async Task RestoredCompletedFileUsesAlternateWhenOwnerProviderIsDisabled()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1, false), new Row("b", 2));
        var registry = new NativeStreamProxyRegistry(orchestrator);
        var owner = new DebridCompletedFileHandle("a", "remote-a", "1", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var alternate = new DebridCompletedFileHandle("b", "remote-b", "7", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var key = "0123456789abcdef0123456789abcdef";

        Assert.True(registry.RestoreCompletedFile(key, owner, Guid.NewGuid(), null, [alternate]));
        var resolved = await registry.ResolveTargetAsync(key, false, default);

        Assert.Equal("b", resolved.Stream!.CompletedFileHandle!.ProviderId);
        Assert.Equal(0, a.CompletedFileCalls);
    }

    [Fact]
    public void RestoredCompletedFileIsServableOnlyWhileSomeHandleHasAUsableProvider()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1, false), new Row("b", 2));
        var registry = new NativeStreamProxyRegistry(orchestrator);
        var owner = new DebridCompletedFileHandle("a", "remote-a", "1", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var alternate = new DebridCompletedFileHandle("b", "remote-b", "7", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var key = "0123456789abcdef0123456789abcdef";

        // The entry is kept either way; only the promise to a client differs.
        Assert.True(registry.RestoreCompletedFile(key, owner, Guid.NewGuid(), null));
        Assert.False(registry.CanServeCompletedFile(owner));
        Assert.True(registry.CanServeCompletedFile(owner, [alternate]));
    }

    [Fact]
    public void JobIdentityIsProviderNeutralAndLegacyTorBoxJobsStillMatch()
    {
        var itemId = Guid.NewGuid();
        var legacy = new DebridCompletedFileHandle("torbox", "123", "9", "Movie.2026.mkv", 1_000);
        var rich = new DebridCompletedFileHandle("torbox", "123", "9", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var other = new DebridCompletedFileHandle("realdebrid", "ABC", "1", "Movie.2026.mkv", 1_000, HashA, File1.Name);

        var legacyJob = Job(itemId, legacy, AcquisitionJobIdentity.Create(itemId, legacy));

        Assert.Equal(AcquisitionJobIdentity.Create(itemId, rich), AcquisitionJobIdentity.Create(itemId, other));
        Assert.NotEqual(AcquisitionJobIdentity.Create(itemId, legacy), AcquisitionJobIdentity.Create(itemId, rich));
        Assert.True(AcquisitionJobIdentity.MatchesAny(legacyJob, AcquisitionJobIdentity.Candidates(itemId, rich)));

        var upgraded = AcquisitionCoordinator.AttachHandle(legacyJob, rich, AcquisitionJobIdentity.Create(itemId, rich));
        var withAlternate = AcquisitionCoordinator.AttachHandle(upgraded, other, AcquisitionJobIdentity.Create(itemId, other));

        Assert.Equal(legacyJob.Id, withAlternate.Id);
        Assert.Equal("torbox", withAlternate.CompletedFileHandle!.ProviderId);
        Assert.NotNull(withAlternate.CompletedFileHandle.ContentIdentity);
        Assert.Equal(["realdebrid"], withAlternate.AlternateHandles!.Select(h => h.ProviderId));
        Assert.Equal(AcquisitionJobIdentity.Create(itemId, rich), withAlternate.CacheIdentity);
    }

    [Fact]
    public void AttachHandleRefusesAnAlternateWithADifferentLength()
    {
        var itemId = Guid.NewGuid();
        var owner = new DebridCompletedFileHandle("a", "1", "1", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var wrong = new DebridCompletedFileHandle("b", "2", "2", "Movie.2026.mkv", 1_001, HashA, File1.Name);

        var job = AcquisitionCoordinator.AttachHandle(Job(itemId, owner, "id"), wrong, "id");

        Assert.Null(job.AlternateHandles);
    }

    [Fact]
    public async Task JobsSurviveRestartAndCarryNoCredentialsOrSignedUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), "nebula-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var itemId = Guid.NewGuid();
            var owner = new DebridCompletedFileHandle("a", "1", "1", "Movie.2026.mkv", 1_000, HashA, File1.Name);
            var alternate = new DebridCompletedFileHandle("b", "2", "2", "Movie.2026.mkv", 1_000, HashA, File1.Name);
            var job = Job(itemId, owner, AcquisitionJobIdentity.Create(itemId, owner)) with { AlternateHandles = [alternate] };

            await new AcquisitionJobStore(new TestPaths(root)).UpsertAsync(job, default);
            var reloaded = (await new AcquisitionJobStore(new TestPaths(root)).ReadAsync(default)).Single();
            var raw = await File.ReadAllTextAsync(Path.Combine(root, "nebulabridge", "acquisition", "jobs.json"));

            Assert.Equal(job.Id, reloaded.Id);
            Assert.Equal(owner, reloaded.CompletedFileHandle);
            Assert.Equal([alternate], reloaded.AlternateHandles!);
            Assert.DoesNotContain("https://", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LegacyJobJsonWithoutAlternateHandlesStillLoads()
    {
        var json = """
            {"id":"6a8d2b0c-1111-4222-8333-444444444444","itemId":"6a8d2b0c-5555-4666-8777-888888888888","providerId":"torbox","sourceId":"x","fileName":"a.mkv","state":0,"createdUtc":"2026-01-01T00:00:00+00:00","updatedUtc":"2026-01-01T00:00:00+00:00","completedFileHandle":{"providerId":"torbox","remoteItemId":"1","fileId":"2","fileName":"a.mkv","expectedLength":10}}
            """;
        var job = System.Text.Json.JsonSerializer.Deserialize<AcquisitionJob>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(job);
        Assert.Null(job!.AlternateHandles);
        Assert.Null(job.CompletedFileHandle!.ContentIdentity);
    }

    [Fact]
    public void CompletionRoutesSkipDisabledOwnerButKeepImportedIdentity()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var b = new ScriptedProvider("b", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a, b], new Row("a", 1, false), new Row("b", 2));
        var coordinator = new AcquisitionCoordinator(null!, null!, orchestrator, null!, null!, null!, null!, NullLogger<AcquisitionCoordinator>.Instance);
        var itemId = Guid.NewGuid();
        var owner = new DebridCompletedFileHandle("a", "1", "1", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var alternate = new DebridCompletedFileHandle("b", "2", "2", "Movie.2026.mkv", 1_000, HashA, File1.Name);
        var job = Job(itemId, owner, "id") with { AlternateHandles = [alternate], Imported = true };

        var routes = coordinator.GetCompletionRoutes(job);

        Assert.Equal(["b"], routes.Select(r => r.Provider.Id));
        Assert.True(job.Imported);
        Assert.Equal("a", job.CompletedFileHandle!.ProviderId);
    }

    [Fact]
    public void ProviderDescriptionsNeverContainCredentials()
    {
        var a = new ScriptedProvider("a", cached: [(HashA, File1)]);
        var orchestrator = Orchestrate([a], new Row("a", 1, true, "super-secret-token"));

        var description = orchestrator.DescribeProviders().Single();
        var serialized = System.Text.Json.JsonSerializer.Serialize(description);

        Assert.True(description.HasCredential);
        Assert.DoesNotContain("super-secret-token", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthClassificationMapsReasonsToBoundedBackoff()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var tracker = new DebridProviderHealthTracker(() => now);

        Assert.Equal(DebridProviderHealth.AuthFailed, tracker.ReportFailure("a", "authentication_rejected"));
        Assert.Equal(DebridProviderHealth.PremiumUnavailable, tracker.ReportFailure("b", "premium_required"));
        Assert.Equal(DebridProviderHealth.Healthy, tracker.ReportFailure("c", "not_cached"));
        Assert.Equal(DebridProviderHealth.TemporarilyUnavailable, tracker.ReportFailure("d", "provider_unavailable"));

        Assert.False(tracker.IsAvailable("a"));
        Assert.True(tracker.IsAvailable("c"));
        now = now.AddHours(1);
        Assert.True(tracker.IsAvailable("a"));
        Assert.True(tracker.IsAvailable("b"));
        Assert.True(tracker.IsAvailable("d"));
    }

    [Fact]
    public void ConfigurationMigratesLegacyTorBoxFlagAndKeepsUnknownProvidersOut()
    {
        var cfg = new PluginConfiguration { EnableTorBoxResolver = true };
        cfg.DebridProviders.Add(new DebridProviderConfig { Id = "RealDebrid", Enabled = true, Priority = 0 });
        cfg.DebridProviders.Add(new DebridProviderConfig { Id = "realdebrid", Enabled = false, Priority = 5 });

        cfg.NormalizeForRuntime();

        var torbox = cfg.GetDebridProvider("torbox");
        var rd = cfg.GetDebridProvider("realdebrid");
        Assert.NotNull(torbox);
        Assert.True(torbox!.Enabled);
        Assert.NotNull(rd);
        Assert.Equal(1, rd!.Priority);
        Assert.Equal(2, cfg.DebridProviders.Count);
    }

    private sealed record Row(string Id, int Priority, bool Enabled = true, string Credential = "secret");

    private static DebridProviderOrchestrator Orchestrate(IEnumerable<IDebridProvider> providers, params Row[] rows)
    {
        var list = providers.ToList();
        var settings = rows.Length == 0
            ? Settings(list.Select((p, i) => (p.Id, i + 1)).ToArray())
            : new StaticSettings(rows.ToDictionary(r => r.Id, r => new DebridProviderSettings(r.Id, r.Enabled, r.Priority, r.Credential), StringComparer.OrdinalIgnoreCase));
        return new DebridProviderOrchestrator(list, settings, new DebridProviderHealthTracker(), () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), TimeSpan.Zero);
    }

    private static IDebridProviderSettingsProvider Settings(params (string Id, int Priority)[] rows) =>
        new StaticSettings(rows.ToDictionary(r => r.Id, r => new DebridProviderSettings(r.Id, true, r.Priority, "secret"), StringComparer.OrdinalIgnoreCase));

    private static NativeReleaseCandidate Candidate(string hash, DebridAvailabilityLookup? lookup = null) =>
        new("indexer", "Movie 2026", new Uri("magnet:?xt=urn:btih:" + hash), "torrent", hash, 1_000)
        {
            Availability = lookup?.For(hash),
            Playable = lookup?.For(hash).Any(r => r.Cached) ?? false,
        };

    private static AcquisitionJob Job(Guid itemId, DebridCompletedFileHandle handle, string cacheIdentity) =>
        new(
            Guid.NewGuid(),
            itemId,
            handle.ProviderId,
            "debrid:indexer",
            handle.FileName,
            AcquisitionJobState.Completed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ExpectedBytes: handle.ExpectedLength,
            CompletedFileHandle: handle,
            CacheIdentity: cacheIdentity);

    private sealed class StaticSettings(IReadOnlyDictionary<string, DebridProviderSettings> rows) : IDebridProviderSettingsProvider
    {
        public DebridProviderSettings GetSettings(string providerId) =>
            rows.TryGetValue(providerId, out var row) ? row : new DebridProviderSettings(providerId, false, 100, string.Empty);
    }

    private sealed class ScriptedProvider(
        string id,
        IReadOnlyList<(string Hash, DebridFile File)>? cached = null,
        NativeSourceFailure? failure = null,
        Exception? exception = null,
        NativeSourceFailure? playbackFailure = null
    ) : IDebridProvider
    {
        private int _urlCounter;

        public string Id => id;

        public string Name => id.ToUpperInvariant();

        public bool Enabled => true;

        public bool Configured => true;

        public NativeSourceFailure? PlaybackFailure { get; set; } = playbackFailure;

        public int CacheChecks { get; private set; }

        public int CompletedFileCalls { get; private set; }

        public DebridProviderCapabilities Capabilities =>
            DebridProviderCapabilities.CachedAvailability
            | DebridProviderCapabilities.FileSelection
            | DebridProviderCapabilities.DirectStreamUrl
            | DebridProviderCapabilities.CompletedFileSource;

        public Task<DebridCacheCheckResult> CheckCachedAsync(IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken)
        {
            CacheChecks++;
            if (exception is not null) throw exception;
            var map = new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase);
            if (failure is null)
            {
                foreach (var hash in infoHashes)
                {
                    var files = (cached ?? []).Where(c => string.Equals(c.Hash, hash, StringComparison.OrdinalIgnoreCase)).Select(c => c.File).ToList();
                    map[hash] = new DebridAvailability(id, files.Count > 0, files) { RemoteItemId = files.Count > 0 ? "remote-" + id : null };
                }
            }

            return Task.FromResult(new DebridCacheCheckResult(map, failure));
        }

        public Task<DebridPlaybackResult> ResolvePlaybackAsync(NativeReleaseCandidate candidate, NativeMediaQuery query, CancellationToken cancellationToken) =>
            ResolvePlaybackAsync(candidate, query, null, cancellationToken);

        public Task<DebridPlaybackResult> ResolvePlaybackAsync(NativeReleaseCandidate candidate, NativeMediaQuery query, DebridFileIdentity? pinnedFile, CancellationToken cancellationToken)
        {
            if (PlaybackFailure is not null)
                return Task.FromResult(new DebridPlaybackResult(null, PlaybackFailure));
            var files = (cached ?? []).Where(c => string.Equals(c.Hash, candidate.InfoHash, StringComparison.OrdinalIgnoreCase)).Select(c => c.File).ToList();
            var selection = pinnedFile is null
                ? DebridMediaFileSelector.SelectWithDiagnostics(files, query)
                : DebridMediaFileSelector.SelectPinned(files, candidate.InfoHash, pinnedFile);
            if (selection.File is null)
                return Task.FromResult(new DebridPlaybackResult(null, new NativeSourceFailure(id, selection.Message, Reason: selection.Reason)));
            var handle = new DebridCompletedFileHandle(id, "remote-" + id, selection.File.Id.ToString(), Path.GetFileName(selection.File.Name), selection.File.SizeBytes, candidate.InfoHash, selection.File.Name);
            return Task.FromResult(new DebridPlaybackResult(new NativeResolvedStream(
                $"{id}:{candidate.SourceId}",
                candidate.Title,
                new Uri($"https://{id}.invalid/{Interlocked.Increment(ref _urlCounter)}/file.mkv"),
                selection.File.SizeBytes,
                Path.GetFileName(selection.File.Name),
                handle)));
        }

        public Task<DebridCompletedFileSource?> ResolveCompletedFileSourceAsync(DebridCompletedFileHandle handle, CancellationToken cancellationToken)
        {
            CompletedFileCalls++;
            return Task.FromResult<DebridCompletedFileSource?>(
                new DebridCompletedFileSource(new Uri($"https://{id}.invalid/{Interlocked.Increment(ref _urlCounter)}/file.mkv"), handle.ExpectedLength, true));
        }
    }

    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;
        public string WebPath => root;
        public string ProgramSystemPath => root;
        public string DataPath => root;
        public string ImageCachePath => root;
        public string PluginsPath => root;
        public string PluginConfigurationsPath => root;
        public string LogDirectoryPath => root;
        public string ConfigurationDirectoryPath => root;
        public string SystemConfigurationFilePath => Path.Combine(root, "system.xml");
        public string CachePath => root;
        public string TempDirectory => root;
        public string VirtualDataPath => root;
        public string TrickplayPath => root;
        public string BackupPath => root;
        public void MakeSanityCheckOrThrow() => Directory.CreateDirectory(root);
        public void CreateAndCheckMarker(string path, string markerName, bool recursive) => Directory.CreateDirectory(path);
    }
}
