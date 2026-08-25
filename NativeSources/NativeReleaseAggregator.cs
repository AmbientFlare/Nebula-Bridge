using System.Text.RegularExpressions;

namespace NebulaBridge.NativeSources;

public sealed partial class NativeReleaseAggregator
{
    public IReadOnlyList<NativeReleaseCandidate> Aggregate(
        IEnumerable<NativeReleaseCandidate> candidates,
        int perSourceLimit = 20,
        int globalLimit = 50
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perSourceLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(perSourceLimit, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(globalLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(globalLimit, 200);

        var normalized = candidates.Select(candidate =>
            candidate with
            {
                InfoHash = CardigannResultNormalizer.NormalizeInfoHash(candidate.InfoHash),
            }
        );
        var ranked = normalized
            .GroupBy(DeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                var selected = items
                    .OrderByDescending(item => item.Kind == "http")
                    .ThenByDescending(item => item.Seeders ?? -1)
                    .ThenByDescending(item => item.PublishedAt)
                    .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
                    .First();
                var sources = items
                    .Select(item => new NativeResultSource(
                        item.SourceId,
                        string.IsNullOrWhiteSpace(item.SourceName) ? item.SourceId : item.SourceName,
                        item.Link,
                        item.DefinitionVersion,
                        item.DefinitionHash
                    ))
                    .DistinctBy(
                        source => $"{source.IndexerId}\n{source.Link.AbsoluteUri}",
                        StringComparer.OrdinalIgnoreCase
                    )
                    .OrderBy(source => source.IndexerId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return selected with { Sources = sources };
            })
            .OrderByDescending(item => item.Kind == "http")
            .ThenByDescending(item => item.Seeders ?? -1)
            .ThenByDescending(item => item.SizeBytes ?? -1)
            .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var output = new List<NativeReleaseCandidate>(globalLimit);
        foreach (var candidate in ranked)
        {
            var count = counts.GetValueOrDefault(candidate.SourceId);
            if (count >= perSourceLimit)
            {
                continue;
            }

            counts[candidate.SourceId] = count + 1;
            output.Add(candidate);
            if (output.Count == globalLimit)
            {
                break;
            }
        }

        return output;
    }

    private static string DeduplicationKey(NativeReleaseCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.InfoHash))
        {
            return $"hash:{candidate.InfoHash.ToLowerInvariant()}";
        }

        if (candidate.Link.IsAbsoluteUri)
        {
            var builder = new UriBuilder(candidate.Link) { Fragment = string.Empty };
            return $"uri:{builder.Uri.AbsoluteUri}";
        }

        return $"release:{NormalizePattern().Replace(candidate.Title, string.Empty).ToLowerInvariant()}:{candidate.SizeBytes}";
    }

    [GeneratedRegex("[^\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizePattern();
}
