using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class NativeIndexerClientTests
{
    [Fact]
    public void InvalidTlsCertificateRequiresExactDefinitionPin()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=wrong-host.example",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA1);

        Assert.True(NativeIndexerClient.CertificateIsAccepted(
            certificate,
            SslPolicyErrors.RemoteCertificateNameMismatch,
            new HashSet<string>([fingerprint], StringComparer.OrdinalIgnoreCase)
        ));
        Assert.False(NativeIndexerClient.CertificateIsAccepted(
            certificate,
            SslPolicyErrors.RemoteCertificateNameMismatch,
            new HashSet<string>([new string('0', 40)], StringComparer.OrdinalIgnoreCase)
        ));
        Assert.True(NativeIndexerClient.CertificateIsAccepted(
            certificate,
            SslPolicyErrors.None,
            new HashSet<string>()
        ));
        Assert.False(NativeIndexerClient.CertificateIsAccepted(
            certificate,
            SslPolicyErrors.None,
            new HashSet<string>([new string('0', 40)], StringComparer.OrdinalIgnoreCase)
        ));
    }

    [Fact]
    public async Task ExecutesHtmlCssGetAndNormalizesTorrentFields()
    {
        const string html = """
            <article class="result">
              <a class="title">Example Show S01E02 1080p</a>
              <a class="download" href="/files/example.torrent">download</a>
              <a class="details" href="/details/1">details</a>
              <span class="size">1.5 GiB</span>
              <span class="seeders">42</span><span class="leechers">3</span>
              <time>2026-08-23T10:00:00Z</time><span class="uploader">tester</span>
            </article>
            """;
        var client = CardigannTestSupport.CreateClient(request =>
        {
            Assert.Equal("Example Show S01E02", QueryValue(request.RequestUri!, "q"));
            return Ok(html);
        });
        var definition = CardigannTestSupport.BuildDefinition(
            "html-fixture", "html", ".result",
            Object("""
                {"title":{"selector":".title"},"download":{"selector":".download","attribute":"href"},
                 "details":{"selector":".details","attribute":"href"},"size":{"selector":".size"},
                 "seeders":{"selector":".seeders"},"leechers":{"selector":".leechers"},
                 "date":{"selector":"time"},"uploader":{"selector":".uploader"}}
                """),
            Object("""{"q":"{{ .Keywords }}"}""")
        );

        var result = Assert.Single(await client.SearchAsync(
            definition, new NativeMediaQuery("Example Show", Season: 1, Episode: 2),
            CancellationToken.None));

        Assert.Equal("torrent", result.Kind);
        Assert.Equal(1610612736, result.SizeBytes);
        Assert.Equal(42, result.Seeders);
        Assert.Equal(3, result.Leechers);
        Assert.Equal(45, result.Peers);
        Assert.Equal("tester", result.Uploader);
        Assert.Equal("https://example.com/files/example.torrent", result.DownloadUrl?.AbsoluteUri);
        Assert.Equal("https://example.com/details/1", result.DetailsUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task ExecutesJsonPostWithStructuredMetadataAndHeaders()
    {
        const string json = """
            {"data":{"results":[{"name":"Dune 2021",
              "hash":"0123456789abcdef0123456789abcdef01234567","bytes":734003200,
              "published":1724371200,"seeds":12}]}}
            """;
        var client = CardigannTestSupport.CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("tt1160419", request.Headers.GetValues("X-Test").Single());
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("title=Dune", body, StringComparison.Ordinal);
            Assert.Contains("year=2021", body, StringComparison.Ordinal);
            Assert.Contains("imdb=tt1160419", body, StringComparison.Ordinal);
            return Ok(json);
        });
        var definition = CardigannTestSupport.BuildDefinition(
            "json-fixture", "json", "data.results",
            Object("""
                {"title":{"selector":"name"},"infohash":{"selector":"hash"},
                 "size":{"selector":"bytes"},"date":{"selector":"published"},
                 "seeders":{"selector":"seeds"}}
                """),
            Object("""{"title":"{{ .Query.Q }}","year":"{{ .Query.Year }}","imdb":"{{ .Query.IMDBID }}"}"""),
            "post", Object("""{"X-Test":["{{ .Query.IMDBID }}"]}""")
        );

        var result = Assert.Single(await client.SearchAsync(
            definition, new NativeMediaQuery("Dune", 2021, ImdbId: "tt1160419"),
            CancellationToken.None));

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
        Assert.Equal(734003200, result.SizeBytes);
        Assert.Equal(12, result.Seeders);
        Assert.NotNull(result.PublishedAt);
        Assert.StartsWith("magnet:?xt=urn:btih:", result.MagnetUrl?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpandsJsonAttributeRowsAndUsesParentMovieFields()
    {
        const string json = """
            {"data":{"movies":[
              {"title_long":"Moby-Dick (1956)","year":1956,"url":"https://example.com/movie/1",
               "torrents":[
                 {"quality":"720p","hash":"0123456789abcdef0123456789abcdef01234567","size_bytes":734003200,"seeds":8},
                 {"quality":"1080p","hash":"89abcdef0123456789abcdef0123456789abcdef","size_bytes":1572864000,"seeds":12}
               ]},
              {"title_long":"Missing torrents","year":2026}
            ]}}
            """;
        var definition = CardigannTestSupport.BuildDefinition(
            "json-multiple-fixture",
            "json",
            "data.movies",
            Object("""
                {"_quality":{"selector":"quality"},
                 "title":{"selector":"..title_long","filters":[{"name":"append","args":" {{ .Result._quality }}"}]},
                 "details":{"selector":"..url"},"infohash":{"selector":"hash"},
                 "size":{"selector":"size_bytes"},"seeders":{"selector":"seeds"}}
                """)
        );
        var rows = definition.Document["search"]!["rows"]!.AsObject();
        rows["attribute"] = "torrents";
        rows["multiple"] = true;
        rows["missingAttributeEqualsNoResults"] = true;

        var results = await CardigannTestSupport.CreateClient(_ => Ok(json)).SearchAsync(
            definition,
            new NativeMediaQuery("Moby-Dick"),
            CancellationToken.None
        );

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.Title == "Moby-Dick (1956) 720p");
        Assert.Contains(results, result => result.Title == "Moby-Dick (1956) 1080p");
        Assert.All(results, result => Assert.NotNull(result.InfoHash));
    }

    [Fact]
    public async Task ExecutesXmlSelectorsAndExtractsMagnetInfoHash()
    {
        const string xml = """
            <rss><channel><item><title>Public Domain Film</title>
              <link>magnet:?xt=urn:btih:89ABCDEF0123456789ABCDEF0123456789ABCDEF</link>
              <pubDate>Fri, 23 Aug 2024 00:00:00 GMT</pubDate>
            </item></channel></rss>
            """;
        var definition = CardigannTestSupport.BuildDefinition(
            "xml-fixture", "xml", "rss > channel > item",
            Object("""{"title":{"selector":"title"},"download":{"selector":"link"},"date":{"selector":"pubDate"}}""")
        );

        var result = Assert.Single(await CardigannTestSupport.CreateClient(_ => Ok(xml))
            .SearchAsync(definition, new NativeMediaQuery("Film"), CancellationToken.None));

        Assert.Equal("89abcdef0123456789abcdef0123456789abcdef", result.InfoHash);
        Assert.Equal("torrent", result.Kind);
        Assert.NotNull(result.PublishedAt);
    }

    [Fact]
    public async Task ExecutesKeywordFiltersOptionalSettingsAndConfiguredAbsoluteApiHost()
    {
        const string json = """
            [{"id":"42","name":"Its Example","info_hash":"0123456789abcdef0123456789abcdef01234567"}]
            """;
        var definition = new IndexerDefinition
        {
            Id = "cardigann-settings-fixture",
            Name = "Cardigann settings fixture",
            Type = "public",
            Links = ["https://primary.example/", "https://legacy.example/"],
            SourcePath = "fixture.yml",
            Document = Object("""
                {
                  "settings":[
                    {"name":"apiurl","type":"text","default":"api.example"},
                    {"name":"uploader","type":"text"}
                  ],
                  "search":{
                    "keywordsfilters":[
                      {"name":"re_replace","args":["(?i)\\bits\\b","it-is"]},
                      {"name":"tolower"}
                    ],
                    "paths":[{"path":"https://{{ .Config.apiurl }}/q?q={{ .Keywords }}","response":{"type":"json"}}],
                    "rows":{"selector":"$"},
                    "fields":{
                      "_id":{"selector":"id"},
                      "title":{"selector":"name"},
                      "infohash":{"selector":"info_hash"},
                      "details":{"text":"{{ .Config.sitelink }}description.php?id={{ .Result._id }}"}
                    }
                  }
                }
                """),
        };
        var client = CardigannTestSupport.CreateClient(request =>
        {
            Assert.Equal("api.example", request.RequestUri!.Host);
            Assert.Equal("it-is example", QueryValue(request.RequestUri, "q"));
            return Ok(json);
        });

        var result = Assert.Single(
            await client.SearchAsync(
                definition,
                new NativeMediaQuery("Its Example"),
                CancellationToken.None
            )
        );

        Assert.Equal("https://primary.example/description.php?id=42", result.DetailsUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task ResolvesSimpleDownloadInfoHashFlowFromDetailsPage()
    {
        const string searchHtml = """
            <div class="row-part">
              <a class="title">Alice in Wonderland</a>
            </div>
            <div class="row-part">
              <a class="details" href="/book/1">details</a>
            </div>
            """;
        const string detailsHtml = """
            <html><body>
              <div id="content"><div class="poststuff"><div class="postname">Alice in Wonderland</div></div></div>
              <table><tr><td>Info Hash:</td><td>0123456789ABCDEF0123456789ABCDEF01234567</td></tr></table>
            </body></html>
            """;
        var requestCount = 0;
        var client = CardigannTestSupport.CreateClient(request =>
        {
            requestCount++;
            return request.RequestUri!.AbsolutePath == "/book/1"
                ? Ok(detailsHtml)
                : Ok(searchHtml);
        });
        var definition = CardigannTestSupport.BuildDefinition(
            "download-infohash-fixture",
            "html",
            ".row-part",
            Object("""
                {"title":{"selector":".title"},
                 "download":{"selector":".details","attribute":"href"},
                 "details":{"selector":".details","attribute":"href"}}
                """)
        );
        definition.Document["search"]!["rows"]!["after"] = 1;
        definition.Document["download"] = Object("""
            {"infohash":{
              "hash":{"selector":"td:contains(\"Info Hash:\") ~ td","filters":[{"name":"regexp","args":"[A-Fa-f0-9]{40}"}]},
              "title":{"selector":"div#content > div.poststuff > div.postname","filters":[{"name":"trim"}]}
            }}
            """);

        var result = Assert.Single(await client.SearchAsync(
            definition,
            new NativeMediaQuery("Alice in Wonderland"),
            CancellationToken.None
        ));

        Assert.Equal(2, requestCount);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
        Assert.Equal("torrent", result.Kind);
        Assert.StartsWith("magnet:?xt=urn:btih:", result.Link.OriginalString, StringComparison.Ordinal);
        Assert.Null(result.DownloadUrl);
        Assert.Equal("https://example.com/book/1", result.DetailsUrl?.AbsoluteUri);
    }

    [Fact]
    public void RendersSafeTemplatesForMoviesTvAndConfiguration()
    {
        var engine = new CardigannTemplateEngine();
        var context = new CardigannTemplateContext(
            "Dune 2021",
            new Dictionary<string, object?> { ["Year"] = 2021, ["Season"] = null },
            new Dictionary<string, object?> { ["enabled"] = true, ["sort"] = "created_at" },
            new Dictionary<string, string>(), ["Movies", "TV"]
        );

        var value = engine.Render(
            "{{ if .Config.enabled }}{{ .Keywords }} {{ .Config.sort }} {{ join .Categories \" OR \" }}{{ else }}off{{ end }}",
            context);

        Assert.Equal("Dune 2021 created_at Movies OR TV", value);
        Assert.Equal("Severance S02E05", NativeIndexerClient.BuildKeywords(
            new NativeMediaQuery("Severance", Season: 2, Episode: 5)));
    }

    [Theory]
    [InlineData("1.5 GiB", 1610612736L)]
    [InlineData("700 MB", 734003200L)]
    [InlineData("1024", 1024L)]
    public void ConvertsHumanReadableSizes(string value, long expected) =>
        Assert.Equal(expected, CardigannResultNormalizer.ParseSize(value));

    private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();

    private static HttpResponseMessage Ok(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == key)
            {
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : null;
            }
        }

        return null;
    }
}
