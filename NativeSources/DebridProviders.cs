using System.Text.RegularExpressions;

namespace NebulaBridge.NativeSources;

public sealed record DebridFile(
    long Id,
    string Name,
    long? SizeBytes = null,
    string? MimeType = null
);

public sealed record DebridAvailability(
    string Provider,
    bool Cached,
    IReadOnlyList<DebridFile> Files
);

public sealed record DebridCacheCheckResult(
    IReadOnlyDictionary<string, DebridAvailability> Availability,
    NativeSourceFailure? Failure = null
);

public sealed record DebridPlaybackRequest(
    string Provider,
    NativeReleaseCandidate Candidate,
    NativeMediaQuery Query
);

public sealed record DebridPlaybackResult(
    NativeResolvedStream? Stream,
    NativeSourceFailure? Failure = null
);

public interface IDebridProvider
{
    string Id { get; }

    string Name { get; }

    bool Enabled { get; }

    bool Configured { get; }

    Task<DebridCacheCheckResult> CheckCachedAsync(
        IReadOnlyCollection<string> infoHashes,
        CancellationToken cancellationToken
    );

    Task<DebridPlaybackResult> ResolvePlaybackAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    );
}

public static partial class DebridMediaFileSelector
{
    private const double MinimumTitleSimilarity = 0.35;
    private static readonly HashSet<string> PlayableExtensions = new(
        [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".m2ts", ".webm"],
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> StopWords = new(
        ["a", "an", "and", "at", "by", "for", "in", "of", "on", "the", "to", "with"],
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> TechnicalTokens = new(
        [
            "480p", "576p", "720p", "1080p", "1080i", "1440p", "2160p", "4k", "uhd",
            "x264", "x265", "h264", "h265", "hevc", "av1", "xvid", "divx", "vp9",
            "web", "webdl", "webrip", "bluray", "brrip", "bdrip", "remux", "hdtv",
            "dvdrip", "hdr", "hdr10", "dolby", "vision", "dv", "aac", "ac3", "eac3",
            "ddp", "dts", "truehd", "atmos", "proper", "repack", "internal", "multi",
            "season", "episode",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    public static DebridFile? Select(
        IReadOnlyList<DebridFile> files,
        NativeMediaQuery query
    ) => SelectWithDiagnostics(files, query).File;

    internal static DebridFileSelectionResult SelectWithDiagnostics(
        IReadOnlyList<DebridFile> files,
        NativeMediaQuery query
    )
    {
        var playable = files.Where(IsPlayable).ToList();
        if (playable.Count == 0)
        {
            return new(null, "media_file_missing", "The torrent contains no playable video files.");
        }

        var primary = playable.Where(file => !IsExtra(file.Name)).ToList();
        if (primary.Count == 0)
        {
            return new(null, "media_file_missing", "The torrent contains only samples or extras.");
        }
        playable = primary;

        if (query.Season is not null && query.Episode is not null)
        {
            var exact = playable
                .Where(file => MatchesEpisode(file.Name, query.Season.Value, query.Episode.Value))
                .ToList();
            if (exact.Count == 0)
            {
                return new(
                    null,
                    "media_file_missing_or_ambiguous",
                    $"No file matched S{query.Season:00}E{query.Episode:00}."
                );
            }

            return SelectTitleMatch(exact, query);
        }

        return SelectTitleMatch(playable, query);
    }

    internal static bool IsPlayable(DebridFile file) =>
        file.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true
        || PlayableExtensions.Contains(Path.GetExtension(file.Name));

    private static DebridFileSelectionResult SelectTitleMatch(
        IReadOnlyList<DebridFile> files,
        NativeMediaQuery query
    )
    {
        var evaluated = files
            .Select(file => (File: file, Validation: ValidateTitle(file.Name, query)))
            .ToList();
        var selected = evaluated
            .Where(item => item.Validation.Accepted)
            .OrderByDescending(item => item.Validation.Similarity)
            .ThenByDescending(item => TitleScore(item.File.Name, query))
            .ThenByDescending(item => item.File.SizeBytes ?? -1)
            .FirstOrDefault();
        if (selected.File is not null)
        {
            return new(selected.File, "matched", selected.Validation.Signal);
        }

        var closest = evaluated.OrderByDescending(item => item.Validation.Similarity).First();
        return new(
            null,
            "media_title_mismatch",
            $"File '{Path.GetFileName(closest.File.Name)}' did not match '{query.Title}' "
                + $"(title similarity {closest.Validation.Similarity:0.00})."
        );
    }

    internal static DebridTitleValidation ValidateTitle(string name, NativeMediaQuery query)
    {
        var expected = TitleTokens(query.Title);
        var actual = TitleTokens(name);
        var imdbId = ImdbIdPattern().Match(query.ImdbId ?? string.Empty).Value;
        if (
            !string.IsNullOrEmpty(imdbId)
            && Normalize(name).Replace(" ", string.Empty, StringComparison.Ordinal)
                .Contains(imdbId, StringComparison.OrdinalIgnoreCase)
        )
        {
            return new(true, 1, "IMDb identifier matched the requested item.");
        }

        if (expected.Count == 0 || actual.Count == 0)
        {
            return new(false, 0, "No meaningful title tokens were available.");
        }

        var intersection = expected.Count(actual.Contains);
        var union = expected.Union(actual, StringComparer.OrdinalIgnoreCase).Count();
        var similarity = union == 0 ? 0 : (double)intersection / union;
        if (intersection > 0 || similarity >= MinimumTitleSimilarity)
        {
            return new(true, similarity, "At least one meaningful title token matched.");
        }

        var compactExpected = string.Concat(expected);
        var compactActual = string.Concat(actual);
        if (
            compactExpected.Length >= 4
            && compactActual.Contains(compactExpected, StringComparison.OrdinalIgnoreCase)
        )
        {
            return new(true, MinimumTitleSimilarity, "Compacted title matched.");
        }

        if (expected.Any(expectedToken =>
            actual.Any(actualToken => AreNearTokens(expectedToken, actualToken))))
        {
            return new(true, MinimumTitleSimilarity, "A near-spelling title token matched.");
        }

        return new(false, similarity, "No meaningful title signal matched.");
    }

    private static int TitleScore(string name, NativeMediaQuery query)
    {
        var normalizedName = Normalize(name);
        var score = TitleTokens(query.Title).Count(word =>
            normalizedName.Contains(word, StringComparison.OrdinalIgnoreCase)
        );
        if (
            query.Year is not null
            && normalizedName.Contains(query.Year.Value.ToString(), StringComparison.Ordinal)
        )
        {
            score += 2;
        }

        return score;
    }

    private static IReadOnlyList<string> TitleTokens(string value)
    {
        var withoutExtension = ExtensionPattern().Replace(value, " ");
        var tokens = Normalize(withoutExtension)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token =>
                !TechnicalTokens.Contains(token)
                && !EpisodeTokenPattern().IsMatch(token)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var meaningful = tokens.Where(token => !StopWords.Contains(token)).ToList();
        return meaningful.Count > 0
            ? meaningful
            : tokens;
    }

    private static bool AreNearTokens(string expected, string actual)
    {
        if (expected.Length < 4 || actual.Length < 4)
        {
            return false;
        }

        var maximumDistance = Math.Max(expected.Length, actual.Length) >= 8 ? 2 : 1;
        if (Math.Abs(expected.Length - actual.Length) > maximumDistance)
        {
            return false;
        }

        return EditDistance(expected, actual, maximumDistance) <= maximumDistance;
    }

    private static int EditDistance(string left, string right, int stopAfter)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            var rowMinimum = current[0];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    substitution
                );
                rowMinimum = Math.Min(rowMinimum, current[rightIndex]);
            }

            if (rowMinimum > stopAfter)
            {
                return rowMinimum;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool MatchesEpisode(string name, int season, int episode)
    {
        var sxe = $@"(?<!\d)s0*{season}e0*{episode}(?!\d)";
        var alternate = $@"(?<!\d)0*{season}x0*{episode}(?!\d)";
        return Regex.IsMatch(name, sxe, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                name,
                alternate,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
    }

    private static bool IsExtra(string name) =>
        ExtraPattern().IsMatch(Path.GetFileNameWithoutExtension(name));

    private static string Normalize(string value) =>
        NonWordPattern().Replace(value, " ").ToLowerInvariant();

    [GeneratedRegex(@"\.[^./\\]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    [GeneratedRegex(@"^(s\d{1,3}e\d{1,3}|\d{1,3}x\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeTokenPattern();

    [GeneratedRegex(@"tt\d{5,12}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImdbIdPattern();

    [GeneratedRegex(
        @"(^|[\s._-])(sample|trailer|teaser|extras?|featurettes?|behind[\s._-]*the[\s._-]*scenes|deleted[\s._-]*scenes)([\s._-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ExtraPattern();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordPattern();
}

internal sealed record DebridFileSelectionResult(
    DebridFile? File,
    string Reason,
    string Message
);

internal sealed record DebridTitleValidation(
    bool Accepted,
    double Similarity,
    string Signal
);
