using NebulaBridge.Config;
using NebulaBridge.Decorators;
using NebulaBridge.Filters;
using NebulaBridge.Providers;
using NebulaBridge.ScheduledTasks;
using NebulaBridge.Services;
using NebulaBridge.NativeSources;
//using IntroDbPlugin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaBridge;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<NextUpSortActionFilter>();
        services.AddSingleton<ImageResourceFilter>();
        services.AddSingleton<DeleteResourceFilter>();
        services.AddSingleton<DownloadFilter>();
        services.AddSingleton<NebulaBridgeManager>();
        services.DecorateSingle<IItemRepository, NebulaBridgeItemRepository>();
        services.AddSingleton(sp => (NebulaBridgeItemRepository)sp.GetRequiredService<IItemRepository>());
        services.AddSingleton<NebulaBridgeStremioProviderFactory>();
        services.AddSingleton<NativeTmdbClient>();
        services.AddSingleton<NebulaBridgeMetadataService>();
        services.AddHostedService<DiscoveryPromotionService>();
        services.AddSingleton<BridgeLibraryService>();
        services.AddHostedService<ManagedLibraryStartupService>();
        services.AddSingleton<UserAccessService>();
        services.AddHostedService(sp => sp.GetRequiredService<UserAccessService>());
        services.AddSingleton(sp => new Lazy<NebulaBridgeManager>(sp.GetRequiredService<NebulaBridgeManager>));
        services.AddSingleton<CatalogService>();
        services.AddSingleton<CatalogImportService>();
        services.AddSingleton<JellyfinTraktAccountBridge>();
        services.AddSingleton<ITraktSettingsProvider, PluginTraktSettingsProvider>();
        services.AddSingleton<TraktHttpClient>();
        services.AddSingleton<NativeTraktClient>();
        services.AddSingleton<TraktNextEpisodesService>();
        services.AddSingleton<PalcoCacheService>();
        services.AddSingleton<IIndexerPreferenceStore, PluginIndexerPreferenceStore>();
        services.AddSingleton<IIndexerDefinitionProvider, LocalIndexerDefinitionProvider>();
        services.AddSingleton<CardigannDefinitionParser>();
        services.AddSingleton<CardigannTemplateEngine>();
        services.AddSingleton<CardigannValueFilters>();
        services.AddSingleton<CardigannResponseParser>();
        services.AddSingleton<IndexerDefinitionLoader>();
        services.AddSingleton<IIndexerCatalogSettings, PluginIndexerCatalogSettings>();
        services.AddSingleton<IndexerCatalogUpdater>();
        services.AddSingleton<IndexerUpdateCoordinator>();
        services.AddSingleton<INetworkTargetValidator, NetworkTargetValidator>();
        services.AddSingleton<NativeIndexerClient>();
        services.AddSingleton<IIndexerSearchEngine>(sp =>
            sp.GetRequiredService<NativeIndexerClient>()
        );
        services.AddSingleton<NativeReleaseAggregator>();
        services.AddSingleton<CardigannSearchCoordinator>();
        services.AddSingleton<IStreamResolver, DirectHttpStreamResolver>();
        services.AddSingleton<ITorBoxSettingsProvider, PluginTorBoxSettingsProvider>();
        services.AddSingleton<TorBoxHttpClient>();
        services.AddSingleton<TorBoxStreamResolver>();
        services.AddSingleton<IDebridProvider>(sp => sp.GetRequiredService<TorBoxStreamResolver>());
        services.AddSingleton<NativeStreamProxyRegistry>();
        services.AddSingleton<NativeStreamProxyHttpClient>();
        services.AddSingleton<NativeSourcePipeline>();
        services
            .AddHttpClient(nameof(NativeIndexerClient))
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                }
            )
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15));
        services
            .AddHttpClient(nameof(IndexerCatalogUpdater))
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(60));
        services
            .AddHttpClient(nameof(NativeTmdbClient), client =>
            {
                client.BaseAddress = new Uri("https://api.themoviedb.org/3/", UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddHostedService<IndexerCatalogRefreshService>();
        services.AddSingleton<IHostedService, NebulaBridgeJavaScriptRegistrationService>();
        services.AddSingleton<SubtitleProvider>();
        services.AddSingleton<ISubtitleProvider>(sp => sp.GetRequiredService<SubtitleProvider>());
        services.AddSingleton(sp => new Lazy<SubtitleProvider>(
            sp.GetRequiredService<SubtitleProvider>
        ));

        // Metadata providers
        services.AddSingleton<NebulaBridgeSeriesProvider>();
        services.AddSingleton<IRemoteMetadataProvider>(sp =>
            sp.GetRequiredService<NebulaBridgeSeriesProvider>()
        );
        services.AddSingleton<NebulaBridgeMovieMetadataProvider>();
        services.AddSingleton<IRemoteMetadataProvider>(sp =>
            sp.GetRequiredService<NebulaBridgeMovieMetadataProvider>()
        );
        services.AddSingleton<NebulaBridgeEpisodeMetadataProvider>();
        services.AddSingleton<IRemoteMetadataProvider>(sp =>
            sp.GetRequiredService<NebulaBridgeEpisodeMetadataProvider>()
        );
        services.AddSingleton<NebulaBridgeSeasonMetadataProvider>();
        services.AddSingleton<IRemoteMetadataProvider>(sp =>
            sp.GetRequiredService<NebulaBridgeSeasonMetadataProvider>()
        );

        // Image provider
        services.AddSingleton<NebulaBridgeImageProvider>();
        services.AddSingleton<IRemoteImageProvider>(sp =>
            sp.GetRequiredService<NebulaBridgeImageProvider>()
        );

        // Register HttpClient for IntroDbClient
        services.AddHttpClient<IntroDbClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.introdb.app");
            client.Timeout = TimeSpan.FromSeconds(IntroDbClient.DefaultTimeoutSeconds);
        });
        services.AddSingleton<IMediaSegmentProvider, IntroDbSegmentProvider>();

        services.AddHostedService<NebulaBridgeService>();
        services
            .DecorateSingle<IDtoService, DtoServiceDecorator>()
            .DecorateSingle<IMediaSourceManager, MediaSourceManagerDecorator>()
            .DecorateSingle<ICollectionManager, CollectionManagerDecorator>()
            .DecorateSingle<IPlaylistManager, PlaylistManagerDecorator>()
            .DecorateSingle<ISubtitleManager, SubtitleManagerDecorator>()
            .DecorateSingle<IProviderManager, ProviderManagerDecorator>()
            .DecorateSingle<IImageProcessor, ImageProcessorDecorator>();
        // Expose the concrete decorator as Lazy so ImageProcessorDecorator can call SaveImageDirect
        // without introducing a circular dependency at construction time.
        services.AddSingleton(sp => new Lazy<ProviderManagerDecorator>(
            () => (ProviderManagerDecorator)sp.GetRequiredService<IProviderManager>()));
        services.AddSingleton(sp => new Lazy<ILibraryManager>(
            sp.GetRequiredService<ILibraryManager>));
        services.AddSingleton(sp => new Lazy<ISubtitleManager>(
            sp.GetRequiredService<ISubtitleManager>
        ));

        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
        {
            o.Filters.AddService<InsertActionFilter>(order: 1);
            o.Filters.AddService<SearchActionFilter>(order: 2);
            o.Filters.AddService<PlaybackInfoFilter>(order: 3);
            o.Filters.AddService<NextUpSortActionFilter>(order: 4);
            o.Filters.AddService<ImageResourceFilter>();
            o.Filters.AddService<DeleteResourceFilter>();
            o.Filters.AddService<DownloadFilter>();
        });
    }

    public class NebulaBridgeService(IConfiguration config, ILogger<NebulaBridgeService> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var analyze = NebulaBridgePlugin.Instance?.Configuration?.FFmpegAnalyzeDuration ?? "5M";
            var probe = NebulaBridgePlugin.Instance?.Configuration?.FFmpegProbeSize ?? "40M";

            config["FFmpeg:probesize"] = probe;
            config["FFmpeg:analyzeduration"] = analyze;

            log.LogInformation(
                "Nebula Bridge: set FFmpeg:probesize={Probe}, FFmpeg:analyzeduration={Analyze}",
                probe,
                analyze
            );
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public static class ServiceCollectionDecorationExtensions
{
    private static object BuildInner(IServiceProvider sp, ServiceDescriptor d)
    {
        if (d.ImplementationInstance is not null)
            return d.ImplementationInstance;
        if (d.ImplementationFactory is not null)
            return d.ImplementationFactory(sp);
        return ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    public static IServiceCollection DecorateSingle<TService, TDecorator>(
        this IServiceCollection services
    )
        where TDecorator : class, TService
    {
        var original = services.LastOrDefault(sd => sd.ServiceType == typeof(TService));
        if (original is null)
            return services; // nothing to decorate

        services.Remove(original);

        services.Add(
            new ServiceDescriptor(
                typeof(TService),
                sp =>
                {
                    var inner = (TService)BuildInner(sp, original);
                    return ActivatorUtilities.CreateInstance<TDecorator>(sp, inner);
                },
                original.Lifetime
            )
        );

        return services;
    }
}
