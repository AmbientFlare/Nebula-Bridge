using System.Net;
using System.Reflection;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Dto;
using NebulaBridge.Config;
using NebulaBridge.Controllers;
using NebulaBridge.Decorators;
using NebulaBridge.Filters;
using NebulaBridge.ScheduledTasks;
using NebulaBridge.Services;
using System.Text.Json.Serialization;

namespace NebulaBridge.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public void PalcoControllerRequiresElevatedPermissions()
    {
        var authorize = typeof(PalcoCacheController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .Single();

        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
    }

    [Fact]
    public void EffectiveConfigurationDoesNotMutateSharedPluginConfiguration()
    {
        var baseConfig = new PluginConfiguration
        {
            Url = "https://catalog.example/manifest.json",
            EnabledNativeIndexerIds = ["one"],
        };

        var effective = baseConfig.GetEffectiveConfig(Guid.Empty);
        effective.Url = "https://changed.example/manifest.json";
        effective.EnabledNativeIndexerIds.Add("two");

        Assert.NotSame(baseConfig, effective);
        Assert.Equal("https://catalog.example/manifest.json", baseConfig.Url);
        Assert.Equal(["one"], baseConfig.EnabledNativeIndexerIds);
    }

    [Fact]
    public void ProviderSecretsAreRedactedFromGenericPluginConfigurationJson()
    {
        var secretProperties = new[]
        {
            nameof(PluginConfiguration.TorBoxApiToken),
            nameof(PluginConfiguration.TraktClientId),
            nameof(PluginConfiguration.TraktClientSecret),
            nameof(PluginConfiguration.TraktAccessToken),
            nameof(PluginConfiguration.TraktRefreshToken),
        };

        foreach (var propertyName in secretProperties)
        {
            var property = typeof(PluginConfiguration).GetProperty(propertyName)!;
            Assert.NotNull(property.GetCustomAttribute<JsonIgnoreAttribute>());
        }
    }

    [Fact]
    public void ElevatedPoliciesProtectSecretAndUserManagementApis()
    {
        Assert.Equal(
            Policies.RequiresElevation,
            typeof(ProviderSecretsController)
                .GetCustomAttribute<AuthorizeAttribute>()!
                .Policy
        );
        Assert.Equal(
            Policies.RequiresElevation,
            typeof(UserAccessController).GetCustomAttribute<AuthorizeAttribute>()!.Policy
        );
    }

    [Fact]
    public void MasterUserRestrictionAlsoDisablesRemoteSearch()
    {
        var userId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserConfigs =
            [
                new UserConfig
                {
                    UserId = userId,
                    NoNebulaBridge = true,
                    DisableSearch = false,
                },
            ],
        };

        Assert.True(config.GetEffectiveConfig(userId).DisableSearch);
    }

    [Theory]
    [InlineData("trakt-movies-trending", "trending", CatalogRefreshCadence.Daily)]
    [InlineData("trakt-shows-popular", "popular", CatalogRefreshCadence.Weekly)]
    [InlineData("trakt-shows-anticipated", "anticipated", CatalogRefreshCadence.Weekly)]
    [InlineData("trakt-next-episodes", "next-episodes", CatalogRefreshCadence.TwiceDaily)]
    public void CatalogsMapToStableLibraryKeysAndCadences(
        string id,
        string expectedKey,
        CatalogRefreshCadence expectedCadence
    )
    {
        var catalog = new CatalogConfig { Source = "trakt", Id = id, Type = "series" };

        Assert.Equal(expectedKey, BridgeLibraryService.GetCatalogLibraryKey(catalog));
        Assert.Equal(expectedCadence, BridgeLibraryService.GetCadence(catalog));
        Assert.StartsWith(
            NebulaBridgeManager.CatalogTagPrefix,
            CatalogImportService.BuildCatalogTag(catalog),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NextEpisodeScheduleUsesServerLocalOneAmAndOnePm()
    {
        var triggers = new SyncTraktNextUpTask(null!).GetDefaultTriggers().ToList();

        Assert.Equal(2, triggers.Count);
        Assert.All(triggers, trigger =>
            Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfoType.DailyTrigger, trigger.Type)
        );
        Assert.Equal(
            [TimeSpan.FromHours(1).Ticks, TimeSpan.FromHours(13).Ticks],
            triggers.Select(trigger => trigger.TimeOfDayTicks!.Value).ToArray()
        );
    }

    [Fact]
    public void NextEpisodeCatalogSelectsFirstAiredUnwatchedRegularEpisode()
    {
        var episodes = new[]
        {
            new StremioMeta { Id = "special", Season = 0, Episode = 1, Name = "Special" },
            new StremioMeta { Id = "watched", Season = 1, Episode = 1, Name = "Watched" },
            new StremioMeta
            {
                Id = "next",
                Season = 1,
                Episode = 2,
                Name = "Next",
                Released = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
            },
            new StremioMeta
            {
                Id = "future",
                Season = 1,
                Episode = 3,
                Name = "Future",
                Released = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        var next = TraktNextEpisodesService.SelectNextEpisode(
            episodes,
            new HashSet<(int, int)> { (1, 1) },
            new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
        );

        Assert.NotNull(next);
        Assert.Equal((1, 2), (next.Season, next.Episode));
    }

    [Fact]
    public void SettingsPageOmitsLegacyEndpointAndUsesWriteOnlySecretApi()
    {
        var resourceName = typeof(NebulaBridgePlugin)
            .Assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Config.config.html", StringComparison.Ordinal));
        var scriptResourceName = typeof(NebulaBridgePlugin)
            .Assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Config.config.js", StringComparison.Ordinal));
        using var stream = typeof(NebulaBridgePlugin).Assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        using var scriptStream = typeof(NebulaBridgePlugin).Assembly.GetManifestResourceStream(scriptResourceName)!;
        using var scriptReader = new StreamReader(scriptStream);
        var script = scriptReader.ReadToEnd();
        var pageSource = html + "\n" + script;

        Assert.DoesNotContain("txtUrl", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Optional legacy AIOStreams URL", html, StringComparison.Ordinal);
        Assert.Contains("nebulabridge/provider-secrets", pageSource, StringComparison.Ordinal);
        Assert.Contains("No Nebula Bridge", html, StringComparison.Ordinal);
        Assert.Contains("data-controller=\"__plugin/nebulabridge-config-js\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("page.dataset.nebulaInitialized === 'true'", script, StringComparison.Ordinal);
        Assert.Contains("page.onclick = function (event)", script, StringComparison.Ordinal);
        Assert.Contains("class=\"emby-button tab-button", html, StringComparison.Ordinal);
        Assert.Contains("background: transparent !important", html, StringComparison.Ordinal);
        Assert.DoesNotContain("is=\"emby-input\" class=\"emby-input\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("is=\"emby-textarea\" class=\"emby-textarea\"", html, StringComparison.Ordinal);
        Assert.Contains("finally { Dashboard.hideLoadingMsg(); }", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("finally { Dashboard.hideLoadingMsg(); }", StringComparison.Ordinal)
            < script.IndexOf("loadSecretStates().catch", StringComparison.Ordinal),
            "Supplementary status requests must start only after the modal loading overlay is released.");
        Assert.Contains("Affiliate disclosure", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedLibraryArtworkIsEmbedded()
    {
        Assert.Contains(
            typeof(NebulaBridgePlugin).Assembly.GetManifestResourceNames(),
            name => name.EndsWith("Assets.nebula-library-cover.png", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void HomeNextUpSortsNewestPremiereBeforeDormantSeries()
    {
        var old = new BaseItemDto
        {
            Name = "Old next episode",
            SeriesName = "Dormant show",
            PremiereDate = new DateTime(1984, 9, 30, 0, 0, 0, DateTimeKind.Utc),
        };
        var recent = new BaseItemDto
        {
            Name = "New next episode",
            SeriesName = "Current show",
            PremiereDate = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
        };
        var future = new BaseItemDto
        {
            Name = "Unaired placeholder",
            SeriesName = "Current show",
            PremiereDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var sorted = NextUpSortActionFilter
            .SortNewestFirst(
                [old, recent, future],
                new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
            )
            .ToList();

        Assert.Same(recent, sorted[0]);
        Assert.Same(old, sorted[1]);
        Assert.DoesNotContain(future, sorted);
    }

    [Theory]
    [InlineData("nebulabridge://stub/tt0078607:1:1")]
    [InlineData("gelato://stub/tt0078607:1:1")]
    [InlineData("stremio://series/tt0078607:1:1")]
    public void InternalMetadataUrisAreNeverExposedAsPlayableSources(string path)
    {
        Assert.True(MediaSourceManagerDecorator.IsInternalStubPath(path));
        Assert.False(MediaSourceManagerDecorator.IsInternalStubPath("https://media.example/video.mkv"));
    }

    [Fact]
    public void LegacyBlankManagedLibraryArtworkIsRecognizedWithoutReplacingRealArtwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nebula-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var placeholder = Path.Combine(directory, "poster.png");
            File.WriteAllBytes(placeholder, new byte[3277]);
            Assert.True(BridgeLibraryService.IsLegacyBlankArtwork(placeholder));

            File.WriteAllBytes(placeholder, new byte[8192]);
            Assert.False(BridgeLibraryService.IsLegacyBlankArtwork(placeholder));

            var custom = Path.Combine(directory, "folder.png");
            File.WriteAllBytes(custom, new byte[100]);
            Assert.False(BridgeLibraryService.IsLegacyBlankArtwork(custom));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task KeyLockKeepsSameKeyMutuallyExclusiveUnderContention()
    {
        var keyedLock = new KeyLock();
        var key = Guid.NewGuid();
        var inside = 0;
        var maximumInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            keyedLock.RunQueuedAsync(key, async cancellationToken =>
            {
                var current = Interlocked.Increment(ref inside);
                InterlockedExtensions.Max(ref maximumInside, current);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
            })));

        Assert.Equal(1, maximumInside);
    }

    [Fact]
    public void EmptyAuthenticatedUserClaimFallsBackToExplicitUserQuery()
    {
        var expected = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("UserId", string.Empty)],
                    "api-key"
                )
            ),
        };
        context.Request.QueryString = new QueryString($"?userId={expected:D}");

        Assert.True(context.TryGetUserId(out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task StremioRequestPreservesHttpStatusCode()
    {
        var provider = CreateStremioProvider(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt1234567"))
        );

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [Fact]
    public async Task StremioRequestPreservesCancellation()
    {
        var provider = CreateStremioProvider(_ =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException("cancelled")));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetStreamsAsync(new StremioUri(StremioMediaType.Movie, "tt1234567"))
        );
    }

    private static NebulaBridgeStremioProvider CreateStremioProvider(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response
    ) =>
        new(
            "https://catalog.example",
            new HandlerFactory(new DelegateHandler(response)),
            NullLogger<NebulaBridgeStremioProvider>.Instance
        );

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => response(request);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
