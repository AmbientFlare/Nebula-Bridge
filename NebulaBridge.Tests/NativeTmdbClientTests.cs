using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class NativeTmdbClientTests
{
    [Fact]
    public async Task ResolvesImdbMovieAndMapsNativeMetadata()
    {
        var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/find/tt0063350" => Json("""
                {"movie_results":[{"id":10331}],"tv_results":[]}
                """),
            "/3/movie/10331" => Json("""
                {
                  "id":10331,"imdb_id":"tt0063350","title":"Night of the Living Dead",
                  "overview":"The dead walk.","release_date":"1968-10-04","runtime":96,
                  "vote_average":7.6,"poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg",
                  "genres":[{"name":"Horror"}],"origin_country":["US"],
                  "credits":{"cast":[{"name":"Duane Jones","character":"Ben","profile_path":"/duane.jpg"}],"crew":[]},
                  "release_dates":{"results":[]},"videos":{"results":[]},"images":{}
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var result = await client.GetMetaAsync(
            "tt0063350",
            StremioMediaType.Movie,
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal("Night of the Living Dead", result.Name);
        Assert.Equal("10331", result.TmdbId);
        Assert.Equal("tt0063350", result.ImdbId);
        Assert.Equal(1968, result.Year);
        Assert.Equal("https://image.tmdb.org/t/p/original/poster.jpg", result.Poster);
        Assert.Equal("Duane Jones", Assert.Single(result.App_Extras!.Cast!).Name);
    }

    [Fact]
    public async Task BuildsCompleteSeriesTreeFromTmdbSeasons()
    {
        var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/tv/97546" => Json("""
                {
                  "id":97546,"name":"Ted Lasso","overview":"Football is life.",
                  "first_air_date":"2020-08-14","last_air_date":"2023-05-31",
                  "status":"Ended","episode_run_time":[30],"vote_average":8.4,
                  "poster_path":"/ted.jpg","backdrop_path":"/ted-bg.jpg",
                  "genres":[{"name":"Comedy"}],"origin_country":["US"],
                  "external_ids":{"imdb_id":"tt10986410","tvdb_id":383203},
                  "credits":{"cast":[],"crew":[]},"content_ratings":{"results":[]},
                  "videos":{"results":[]},"images":{},
                  "seasons":[{"season_number":1,"poster_path":"/s1.jpg"}]
                }
                """),
            "/3/tv/97546/season/1" => Json("""
                {"episodes":[
                  {"id":2279111,"episode_number":1,"name":"Pilot","overview":"Ted arrives.",
                   "air_date":"2020-08-14","runtime":31,"still_path":"/pilot.jpg","vote_average":8.1},
                  {"id":2279112,"episode_number":2,"name":"Biscuits","overview":"Biscuits.",
                   "air_date":"2020-08-14","runtime":29,"still_path":"/biscuits.jpg","vote_average":8.2}
                ]}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var result = await client.GetMetaAsync(
            "tmdb:97546",
            StremioMediaType.Series,
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal("tt10986410", result.ImdbId);
        Assert.Equal("383203", result.TvdbId);
        Assert.Equal(StremioStatus.Ended, result.Status);
        Assert.Equal(2, result.Videos!.Count);
        Assert.Equal((1, 1), (result.Videos[0].Season, result.Videos[0].Episode));
        Assert.Equal("https://image.tmdb.org/t/p/original/pilot.jpg", result.Videos[0].Thumbnail);
        Assert.Equal("https://image.tmdb.org/t/p/original/s1.jpg", result.App_Extras!.SeasonPosters![1]);
    }

    [Fact]
    public async Task SearchUsesNativeTmdbWithoutLegacyManifest()
    {
        var client = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/search/tv" => Json("""
                {"results":[{"id":97546,"name":"Ted Lasso","first_air_date":"2020-08-14",
                "overview":"Football is life.","vote_average":8.4,"poster_path":"/ted.jpg"}]}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var results = await client.SearchAsync(
            "Ted Lasso",
            StremioMediaType.Series,
            CancellationToken.None
        );

        var result = Assert.Single(results);
        Assert.Equal("tmdb:97546", result.Id);
        Assert.Equal(2020, result.Year);
    }

    private static NativeTmdbClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> response
    ) => new(
        new HandlerFactory(new DelegateHandler(response)),
        NullLogger<NativeTmdbClient>.Instance
    );

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(response(request));
    }
}
