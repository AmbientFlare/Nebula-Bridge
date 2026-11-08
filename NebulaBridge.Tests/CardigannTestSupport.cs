using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

internal static class CardigannTestSupport
{
    public static CardigannDefinitionParser CreateParser() =>
        new();

    public static IndexerDefinitionLoader CreateLoader(
        IIndexerDefinitionProvider provider,
        IIndexerPreferenceStore preferences,
        IFlareSolverrSettings? flareSolverrSettings = null
    ) =>
        new(
            provider,
            CreateParser(),
            preferences,
            flareSolverrSettings ?? new FixedFlareSolverrSettings(),
            NullLogger<IndexerDefinitionLoader>.Instance
        );

    public static NativeIndexerClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory
    )
    {
        var templates = new CardigannTemplateEngine();
        var filters = new CardigannValueFilters(templates);
        var factory = new StubHttpClientFactory(new StubHandler(responseFactory));
        return new NativeIndexerClient(
            factory,
            new AllowTestTargets(),
            templates,
            filters,
            new CardigannResponseParser(templates, filters),
            new FlareSolverrClient(factory, new FixedFlareSolverrSettings()),
            NullLogger<NativeIndexerClient>.Instance
        );
    }

    public static IndexerDefinition BuildDefinition(
        string id,
        string responseType,
        string rowSelector,
        JsonObject fields,
        JsonObject? inputs = null,
        string method = "get",
        JsonObject? headers = null
    )
    {
        var search = new JsonObject
        {
            ["paths"] = new JsonArray(
                new JsonObject
                {
                    ["path"] = "search",
                    ["method"] = method,
                    ["response"] = new JsonObject { ["type"] = responseType },
                }
            ),
            ["rows"] = new JsonObject { ["selector"] = rowSelector },
            ["fields"] = fields,
        };
        if (inputs is not null)
        {
            search["inputs"] = inputs;
        }

        if (headers is not null)
        {
            search["headers"] = headers;
        }

        return new IndexerDefinition
        {
            Id = id,
            Name = id,
            Type = "public",
            Links = ["https://example.com/"],
            Document = new JsonObject { ["search"] = search },
            SourcePath = id + ".yml",
        };
    }

    public static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "CardigannFixtures", fileName);

    internal sealed class MemoryDefinitionProvider(
        IReadOnlyList<IndexerDefinitionSource> sources,
        string directory = "/test/indexers"
    ) : IIndexerDefinitionProvider
    {
        public string Name => "test";

        public string DefinitionsDirectory => directory;

        public IReadOnlyList<IndexerDefinitionSource> Sources { get; set; } = sources;

        public Task<IReadOnlyList<IndexerDefinitionSource>> LoadAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(Sources);
    }

    internal sealed class MemoryPreferenceStore(IEnumerable<string>? enabled = null)
        : IIndexerPreferenceStore
    {
        private readonly HashSet<string> _enabled = new(
            enabled ?? [],
            StringComparer.OrdinalIgnoreCase
        );

        public IReadOnlyCollection<string> GetEnabledIds() => _enabled.ToList();

        public bool SetEnabled(string id, bool enabled)
        {
            if (enabled)
            {
                _enabled.Add(id);
            }
            else
            {
                _enabled.Remove(id);
            }

            return true;
        }
    }

    internal sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(responseFactory(request));
    }

    internal sealed class StubHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class AllowTestTargets : INetworkTargetValidator
    {
        public Task ValidateAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    internal sealed class FixedFlareSolverrSettings(string? endpoint = null)
        : IFlareSolverrSettings
    {
        public Uri? Endpoint { get; } = endpoint is null ? null : new Uri(endpoint);
    }
}
