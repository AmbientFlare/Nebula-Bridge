using System.Net;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class OfficialDefinitionExecutionTests
{
    [Fact]
    public async Task InternetArchiveV11DefinitionExecutesJsonFixture()
    {
        const string response = """
            {"response":{"numFound":1,"docs":[{
              "identifier":"public-domain-film","title":"Public Domain Film",
              "mediatype":"movies","item_size":734003200,"downloads":100,
              "btih":"0123456789abcdef0123456789abcdef01234567",
              "publicdate":"2024-08-23T00:00:00Z"
            }]}}
            """;
        var definition = await LoadOfficialDefinition("internetarchive.yml", "internetarchive");
        var client = CardigannTestSupport.CreateClient(request =>
        {
            Assert.Equal("/advancedsearch.php", request.RequestUri!.AbsolutePath);
            Assert.Contains("Public%20Domain%20Film", request.RequestUri.Query, StringComparison.Ordinal);
            return Ok(response);
        });

        var result = Assert.Single(await client.SearchAsync(
            definition,
            new NativeMediaQuery("Public Domain Film"),
            CancellationToken.None
        ));

        Assert.Equal("internetarchive", result.SourceId);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
        Assert.Equal(734003200, result.SizeBytes);
        Assert.Equal("https://archive.org/details/public-domain-film", result.DetailsUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task LinuxTrackerV11DefinitionExecutesHtmlCssFixture()
    {
        const string response = """
            <table class="lista" width="100%"><tbody><tr>
              <td><a href="index.php?page=torrents&amp;category=311"><img src="/images/categories/linux.png"></a></td>
              <td>
                <a href="index.php?page=torrent-details&amp;id=89abcdef0123456789abcdef0123456789abcdef" title="details">Ubuntu Public ISO</a>
                <table><tbody>
                  <tr><td><strong>Added</strong>23/08/2026</td></tr>
                  <tr><td><strong>Size</strong>2.5 GiB</td></tr>
                  <tr><td><strong>Seeds</strong>27</td></tr>
                  <tr><td><strong>Leeches</strong>4</td></tr>
                  <tr><td><strong>Completed</strong>9</td></tr>
                </tbody></table>
                Linux distribution image
              </td>
            </tr></tbody></table>
            """;
        var source = await LoadOfficialDefinition("linuxtracker.yml", "linuxtracker");
        var definition = new IndexerDefinition
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Language = source.Language,
            Type = source.Type,
            Encoding = source.Encoding,
            RequestDelaySeconds = 0,
            Links = source.Links,
            Document = source.Document,
            SourcePath = source.SourcePath,
        };

        var results = await CardigannTestSupport.CreateClient(_ => Ok(response))
            .SearchAsync(definition, new NativeMediaQuery("Ubuntu"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("linuxtracker", result.SourceId);
        Assert.Equal("89abcdef0123456789abcdef0123456789abcdef", result.InfoHash);
        Assert.Equal(27, result.Seeders);
        Assert.Equal(4, result.Leechers);
        Assert.Equal(2684354560, result.SizeBytes);
    }

    [Fact]
    public async Task ShowRssV11DefinitionExecutesXmlFixture()
    {
        const string response = """
            <rss><channel><item>
              <raw_title>Public Domain Show S01E01 720p</raw_title>
              <link>magnet:?xt=urn:btih:fedcba9876543210fedcba9876543210fedcba98&amp;dn=show</link>
              <pubDate>Fri, 23 Aug 2024 00:00:00 GMT</pubDate>
            </item></channel></rss>
            """;
        var definition = await LoadOfficialDefinition("showrss.yml", "showrss-yml");

        var result = Assert.Single(await CardigannTestSupport.CreateClient(_ => Ok(response))
            .SearchAsync(definition, new NativeMediaQuery("Public Domain Show"), CancellationToken.None));

        Assert.Equal("showrss-yml", result.SourceId);
        Assert.Equal("fedcba9876543210fedcba9876543210fedcba98", result.InfoHash);
        Assert.Equal("2", result.Category);
        Assert.Equal(536870912, result.SizeBytes);
    }

    private static async Task<IndexerDefinition> LoadOfficialDefinition(
        string fileName,
        string id
    )
    {
        var path = CardigannTestSupport.FixturePath(fileName);
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new IndexerDefinitionSource(path, File.ReadAllText(path))]
            ),
            new CardigannTestSupport.MemoryPreferenceStore([id])
        );
        await loader.RefreshAsync(CancellationToken.None);
        return loader.GetRequired(id);
    }

    private static HttpResponseMessage Ok(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };
}
