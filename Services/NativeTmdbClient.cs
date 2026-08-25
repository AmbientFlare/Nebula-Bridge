using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

/// <summary>
/// Native TMDB metadata client. It deliberately maps into the existing metadata model so the
/// virtual-library creation code stays independent of any one metadata transport.
/// </summary>
public sealed class NativeTmdbClient(
    IHttpClientFactory httpClientFactory,
    ILogger<NativeTmdbClient> logger
)
{
    private const string DefaultTmdbApiKey = "4219e299c89411838049ab0dab19ebd5";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (StremioMeta Meta, DateTimeOffset Expires)
    > _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<StremioMeta?> GetMetaAsync(
        string id,
        StremioMediaType mediaType,
        CancellationToken cancellationToken
    )
    {
        var cacheKey = $"{mediaType}:{id}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Meta;
        }

        var tmdbId = await ResolveTmdbIdAsync(id, mediaType, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return null;
        }

        var meta = mediaType switch
        {
            StremioMediaType.Movie => await GetMovieAsync(tmdbId, cancellationToken)
                .ConfigureAwait(false),
            StremioMediaType.Series => await GetSeriesAsync(tmdbId, cancellationToken)
                .ConfigureAwait(false),
            _ => null,
        };

        if (meta is not null)
        {
            _cache[cacheKey] = (meta, DateTimeOffset.UtcNow.Add(CacheTtl));
            _cache[$"{mediaType}:tmdb:{tmdbId}"] = (
                meta,
                DateTimeOffset.UtcNow.Add(CacheTtl)
            );
        }

        return meta;
    }

    public Task<StremioMeta?> GetMetaAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var id = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(id))
        {
            return GetMetaAsync(
                $"tmdb:{id}",
                item.GetBaseItemKind().ToStremio(),
                cancellationToken
            );
        }

        id = item.GetProviderId(MetadataProvider.Imdb);
        return string.IsNullOrWhiteSpace(id)
            ? Task.FromResult<StremioMeta?>(null)
            : GetMetaAsync(id, item.GetBaseItemKind().ToStremio(), cancellationToken);
    }

    public async Task<IReadOnlyList<StremioMeta>> SearchAsync(
        string query,
        StremioMediaType mediaType,
        CancellationToken cancellationToken
    )
    {
        var path = mediaType switch
        {
            StremioMediaType.Movie => "search/movie",
            StremioMediaType.Series => "search/tv",
            _ => null,
        };
        if (path is null || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var document = await GetDocumentAsync(
                $"{path}?query={Uri.EscapeDataString(query)}&include_adult=false",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results
            .EnumerateArray()
            .Select(result => MapSearchResult(result, mediaType))
            .Where(meta => meta is not null)
            .Cast<StremioMeta>()
            .ToArray();
    }

    public async Task EnrichDigitalReleaseDateAsync(
        StremioMeta meta,
        CancellationToken cancellationToken
    )
    {
        if (meta.Type != StremioMediaType.Movie || meta.App_Extras?.ReleaseDates is not null)
        {
            return;
        }

        var tmdbId = meta.TmdbId
            ?? meta.GetProviderIds().GetValueOrDefault(nameof(MetadataProvider.Tmdb));
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return;
        }

        using var document = await GetDocumentAsync(
                $"movie/{Uri.EscapeDataString(tmdbId)}/release_dates",
                cancellationToken
            )
            .ConfigureAwait(false);
        meta.App_Extras ??= new StremioAppExtras();
        meta.App_Extras.ReleaseDates = document.RootElement.Deserialize<TmdbReleaseDatesContainer>(
            JsonOptions
        );
    }

    private async Task<string?> ResolveTmdbIdAsync(
        string id,
        StremioMediaType mediaType,
        CancellationToken cancellationToken
    )
    {
        if (id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            return id["tmdb:".Length..];
        }

        if (id.All(char.IsDigit))
        {
            return id;
        }

        if (!id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var document = await GetDocumentAsync(
                $"find/{Uri.EscapeDataString(id)}?external_source=imdb_id",
                cancellationToken
            )
            .ConfigureAwait(false);
        var property = mediaType == StremioMediaType.Movie ? "movie_results" : "tv_results";
        if (!TryGetProperty(document.RootElement, property, out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            var value = GetInt(result, "id");
            if (value.HasValue)
            {
                return value.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private async Task<StremioMeta?> GetMovieAsync(
        string tmdbId,
        CancellationToken cancellationToken
    )
    {
        using var document = await GetDocumentAsync(
                $"movie/{Uri.EscapeDataString(tmdbId)}?append_to_response=credits,external_ids,release_dates,videos,images",
                cancellationToken
            )
            .ConfigureAwait(false);
        var root = document.RootElement;
        var title = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var imdb = GetString(root, "imdb_id");
        var released = ParseDate(GetString(root, "release_date"));
        var meta = new StremioMeta
        {
            Id = !string.IsNullOrWhiteSpace(imdb) ? imdb : $"tmdb:{tmdbId}",
            Type = StremioMediaType.Movie,
            Name = title,
            Title = title,
            TmdbId = tmdbId,
            ImdbId = imdb,
            Year = released?.Year,
            ReleaseInfo = released?.Year.ToString(CultureInfo.InvariantCulture),
            Released = released,
            Description = GetString(root, "overview"),
            Overview = GetString(root, "overview"),
            Runtime = GetInt(root, "runtime")?.ToString(CultureInfo.InvariantCulture),
            ImdbRating = GetFloat(root, "vote_average"),
            Genres = GetNamedValues(root, "genres"),
            Country = GetFirstString(root, "origin_country"),
            Poster = ImageUrl(GetString(root, "poster_path")),
            Background = ImageUrl(GetString(root, "backdrop_path")),
            App_Extras = BuildExtras(root, isMovie: true),
            TrailerStreams = GetTrailers(root),
        };
        return meta;
    }

    private async Task<StremioMeta?> GetSeriesAsync(
        string tmdbId,
        CancellationToken cancellationToken
    )
    {
        using var document = await GetDocumentAsync(
                $"tv/{Uri.EscapeDataString(tmdbId)}?append_to_response=credits,external_ids,content_ratings,videos,images",
                cancellationToken
            )
            .ConfigureAwait(false);
        var root = document.RootElement;
        var title = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var imdb = TryGetProperty(root, "external_ids", out var externalIds)
            ? GetString(externalIds, "imdb_id")
            : null;
        var tvdb = TryGetProperty(root, "external_ids", out externalIds)
            ? GetInt(externalIds, "tvdb_id")?.ToString(CultureInfo.InvariantCulture)
            : null;
        var firstAired = ParseDate(GetString(root, "first_air_date"));
        var lastAired = ParseDate(GetString(root, "last_air_date"));
        var status = MapStatus(GetString(root, "status"), firstAired);
        var meta = new StremioMeta
        {
            Id = !string.IsNullOrWhiteSpace(imdb) ? imdb : $"tmdb:{tmdbId}",
            Type = StremioMediaType.Series,
            Name = title,
            Title = title,
            TmdbId = tmdbId,
            ImdbId = imdb,
            TvdbId = tvdb,
            Year = firstAired?.Year,
            ReleaseInfo = BuildSeriesReleaseInfo(firstAired, lastAired, status),
            Released = firstAired,
            Description = GetString(root, "overview"),
            Overview = GetString(root, "overview"),
            Runtime = GetFirstInt(root, "episode_run_time")?.ToString(CultureInfo.InvariantCulture),
            ImdbRating = GetFloat(root, "vote_average"),
            Genres = GetNamedValues(root, "genres"),
            Country = GetFirstString(root, "origin_country"),
            Poster = ImageUrl(GetString(root, "poster_path")),
            Background = ImageUrl(GetString(root, "backdrop_path")),
            Status = status,
            App_Extras = BuildExtras(root, isMovie: false),
            TrailerStreams = GetTrailers(root),
            Videos = [],
        };

        if (TryGetProperty(root, "seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in seasons.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seasonNumber = GetInt(season, "season_number");
                if (!seasonNumber.HasValue || seasonNumber.Value < 0)
                {
                    continue;
                }

                var poster = ImageUrl(GetString(season, "poster_path"));
                EnsureSeasonPoster(meta, seasonNumber.Value, poster);
                try
                {
                    using var seasonDocument = await GetDocumentAsync(
                            $"tv/{Uri.EscapeDataString(tmdbId)}/season/{seasonNumber.Value}",
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    AddEpisodes(meta, seasonDocument.RootElement, seasonNumber.Value);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    logger.LogDebug(
                        "TMDB season metadata was not found for tv/{TmdbId}/season/{Season}",
                        tmdbId,
                        seasonNumber.Value
                    );
                }
            }
        }

        return meta;
    }

    private async Task<JsonDocument> GetDocumentAsync(
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.themoviedb.org/3/{relativePath}{(relativePath.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api_key={Uri.EscapeDataString(GetTmdbApiKey())}&language=en-US"
        );
        using var client = httpClientFactory.CreateClient(nameof(NativeTmdbClient));
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "TMDB metadata request failed at {Path} with status {StatusCode}",
                relativePath.Split('?', 2)[0],
                response.StatusCode
            );
            throw new HttpRequestException(
                $"TMDB request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode
            );
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string GetTmdbApiKey()
    {
        try
        {
            var pluginType = Type.GetType(
                "MediaBrowser.Providers.Plugins.Tmdb.Plugin, Jellyfin.Providers",
                throwOnError: false
            );
            var instance = pluginType?.GetProperty("Instance")?.GetValue(null);
            var cfg = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
            var key = cfg?.GetType().GetProperty("TmdbApiKey")?.GetValue(cfg) as string;
            return string.IsNullOrWhiteSpace(key) ? DefaultTmdbApiKey : key;
        }
        catch
        {
            return DefaultTmdbApiKey;
        }
    }

    private static StremioMeta? MapSearchResult(JsonElement result, StremioMediaType type)
    {
        var tmdbId = GetInt(result, "id");
        var title = GetString(result, type == StremioMediaType.Movie ? "title" : "name");
        if (!tmdbId.HasValue || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var released = ParseDate(
            GetString(result, type == StremioMediaType.Movie ? "release_date" : "first_air_date")
        );
        return new StremioMeta
        {
            Id = $"tmdb:{tmdbId.Value}",
            Type = type,
            Name = title,
            Title = title,
            TmdbId = tmdbId.Value.ToString(CultureInfo.InvariantCulture),
            Year = released?.Year,
            ReleaseInfo = released?.Year.ToString(CultureInfo.InvariantCulture),
            Released = released,
            Description = GetString(result, "overview"),
            Overview = GetString(result, "overview"),
            ImdbRating = GetFloat(result, "vote_average"),
            Poster = ImageUrl(GetString(result, "poster_path")),
            Background = ImageUrl(GetString(result, "backdrop_path")),
        };
    }

    private static StremioAppExtras BuildExtras(JsonElement root, bool isMovie)
    {
        var extras = new StremioAppExtras();
        if (TryGetProperty(root, "credits", out var credits))
        {
            if (TryGetProperty(credits, "cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
            {
                extras.Cast = cast.EnumerateArray().Take(30).Select(person => new StremioCast
                {
                    Name = GetString(person, "name"),
                    Character = GetString(person, "character"),
                    Photo = ImageUrl(GetString(person, "profile_path")),
                }).ToList();
            }

            if (TryGetProperty(credits, "crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
            {
                extras.Directors = crew.EnumerateArray()
                    .Where(person => string.Equals(GetString(person, "job"), "Director", StringComparison.OrdinalIgnoreCase))
                    .Select(MapPerson)
                    .ToList();
                extras.Writers = crew.EnumerateArray()
                    .Where(person => string.Equals(GetString(person, "department"), "Writing", StringComparison.OrdinalIgnoreCase))
                    .Select(MapPerson)
                    .ToList();
            }
        }

        if (isMovie && TryGetProperty(root, "release_dates", out var releaseDates))
        {
            extras.ReleaseDates = releaseDates.Deserialize<TmdbReleaseDatesContainer>(JsonOptions);
            extras.Certification = GetUsMovieCertification(releaseDates);
        }
        else if (!isMovie && TryGetProperty(root, "content_ratings", out var ratings))
        {
            extras.Certification = GetUsTvCertification(ratings);
        }

        return extras;
    }

    private static StremioCast MapPerson(JsonElement person) => new()
    {
        Name = GetString(person, "name"),
        Photo = ImageUrl(GetString(person, "profile_path")),
    };

    private static List<StremioTrailerStream> GetTrailers(JsonElement root)
    {
        if (!TryGetProperty(root, "videos", out var videos)
            || !TryGetProperty(videos, "results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Where(video => string.Equals(GetString(video, "site"), "YouTube", StringComparison.OrdinalIgnoreCase))
            .Select(video => new StremioTrailerStream
            {
                Title = GetString(video, "name"),
                YtId = GetString(video, "key"),
            })
            .Where(video => !string.IsNullOrWhiteSpace(video.YtId))
            .ToList();
    }

    private static void AddEpisodes(StremioMeta series, JsonElement season, int seasonNumber)
    {
        if (!TryGetProperty(season, "episodes", out var episodes)
            || episodes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var episode in episodes.EnumerateArray())
        {
            var number = GetInt(episode, "episode_number");
            if (!number.HasValue)
            {
                continue;
            }

            var aired = ParseDate(GetString(episode, "air_date"));
            series.Videos!.Add(new StremioMeta
            {
                Id = $"{series.Id}:{seasonNumber}:{number.Value}",
                Type = StremioMediaType.Episode,
                Name = GetString(episode, "name") ?? $"Episode {number.Value}",
                Title = GetString(episode, "name") ?? $"Episode {number.Value}",
                Season = seasonNumber,
                Episode = number,
                Number = number,
                Released = aired,
                FirstAired = aired,
                Description = GetString(episode, "overview"),
                Overview = GetString(episode, "overview"),
                Runtime = GetInt(episode, "runtime")?.ToString(CultureInfo.InvariantCulture),
                TmdbId = GetInt(episode, "id")?.ToString(CultureInfo.InvariantCulture),
                ImdbRating = GetFloat(episode, "vote_average"),
                Thumbnail = ImageUrl(GetString(episode, "still_path")),
            });
        }
    }

    private static void EnsureSeasonPoster(StremioMeta meta, int season, string? poster)
    {
        meta.App_Extras ??= new StremioAppExtras();
        meta.App_Extras.SeasonPosters ??= [];
        while (meta.App_Extras.SeasonPosters.Count <= season)
        {
            meta.App_Extras.SeasonPosters.Add(null);
        }
        meta.App_Extras.SeasonPosters[season] = poster;
    }

    private static string? GetUsMovieCertification(JsonElement releaseDates)
    {
        if (!TryGetProperty(releaseDates, "results", out var results))
        {
            return null;
        }
        foreach (var country in results.EnumerateArray())
        {
            if (!string.Equals(GetString(country, "iso_3166_1"), "US", StringComparison.OrdinalIgnoreCase)
                || !TryGetProperty(country, "release_dates", out var dates))
            {
                continue;
            }
            return dates.EnumerateArray().Select(date => GetString(date, "certification"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
        return null;
    }

    private static string? GetUsTvCertification(JsonElement ratings)
    {
        if (!TryGetProperty(ratings, "results", out var results))
        {
            return null;
        }
        foreach (var country in results.EnumerateArray())
        {
            if (string.Equals(GetString(country, "iso_3166_1"), "US", StringComparison.OrdinalIgnoreCase))
            {
                return GetString(country, "rating");
            }
        }
        return null;
    }

    private static StremioStatus MapStatus(string? status, DateTime? firstAired)
    {
        if (firstAired > DateTime.UtcNow)
        {
            return StremioStatus.Upcoming;
        }
        return status?.ToLowerInvariant() switch
        {
            "ended" or "canceled" => StremioStatus.Ended,
            "returning series" or "in production" or "planned" or "pilot" => StremioStatus.Continuing,
            _ => StremioStatus.Unknown,
        };
    }

    private static string? BuildSeriesReleaseInfo(DateTime? first, DateTime? last, StremioStatus status)
    {
        if (!first.HasValue)
        {
            return null;
        }
        return status == StremioStatus.Continuing
            ? $"{first.Value.Year}-"
            : last.HasValue && last.Value.Year != first.Value.Year
                ? $"{first.Value.Year}-{last.Value.Year}"
                : first.Value.Year.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ImageUrl(string? path) => string.IsNullOrWhiteSpace(path)
        ? null
        : path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{ImageBaseUrl}{path}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static int? GetFirstInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }
        }
        return null;
    }

    private static float? GetFloat(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetSingle(out var number)
            ? number
            : null;

    private static string? GetFirstString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static List<string> GetNamedValues(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return values.EnumerateArray()
            .Select(value => GetString(value, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed
        )
            ? parsed
            : null;
}
