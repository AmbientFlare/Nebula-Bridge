using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NebulaBridge.NativeSources;

public static partial class CardigannResultNormalizer
{
    public static NativeReleaseCandidate? Normalize(
        IndexerDefinition definition,
        IReadOnlyDictionary<string, string> fields,
        Uri baseUri
    )
    {
        var title = Get(fields, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var magnet = ParseUri(Get(fields, "magnet"), baseUri, allowMagnet: true);
        var download = ParseUri(Get(fields, "download"), baseUri, allowMagnet: true);
        var details = ParseUri(Get(fields, "details"), baseUri, allowMagnet: false);
        if (magnet is null && download?.Scheme == "magnet")
        {
            magnet = download;
            download = null;
        }

        var infoHash = NormalizeInfoHash(Get(fields, "infohash"))
            ?? NormalizeInfoHash(MagnetInfoHash(magnet));
        if (magnet is null && infoHash is not null)
        {
            magnet = new Uri(
                $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(title.Trim())}"
            );
        }

        var primary = magnet ?? download ?? details;
        if (primary is null)
        {
            return null;
        }

        var kind = magnet is not null || infoHash is not null || IsTorrentDownload(download)
            ? "torrent"
            : "unknown";
        var seeders = ParseInteger(Get(fields, "seeders"));
        var leechers = ParseInteger(Get(fields, "leechers"));
        var peers = ParseInteger(Get(fields, "peers"));
        return new NativeReleaseCandidate(
            definition.Id,
            System.Net.WebUtility.HtmlDecode(title.Trim()),
            primary,
            kind,
            infoHash,
            ParseSize(Get(fields, "size")),
            seeders,
            ParseDate(Get(fields, "date")),
            Get(fields, "category"),
            definition.Name,
            magnet,
            download?.Scheme is "http" or "https" ? download : null,
            details,
            leechers,
            peers ?? (seeders.HasValue || leechers.HasValue ? (seeders ?? 0) + (leechers ?? 0) : null),
            Get(fields, "uploader"),
            DefinitionVersion: definition.SchemaVersion,
            DefinitionHash: definition.DefinitionHash
        );
    }

    internal static long? ParseSize(string? value)
    {
        var match = SizePattern().Match(value ?? string.Empty);
        if (
            !match.Success
            || !double.TryParse(
                match.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount
            )
        )
        {
            return null;
        }

        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" or "KB" or "KIB" => 1024D,
            "M" or "MB" or "MIB" => 1024D * 1024,
            "G" or "GB" or "GIB" => 1024D * 1024 * 1024,
            "T" or "TB" or "TIB" => 1024D * 1024 * 1024 * 1024,
            _ => 1D,
        };
        var bytes = amount * multiplier;
        return bytes is >= 0 and <= long.MaxValue ? (long)bytes : null;
    }

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            return DateTimeOffset.UtcNow;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            try
            {
                return trimmed.Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed
        )
            ? parsed
            : null;
    }

    internal static string? NormalizeInfoHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = HexHashPattern().Match(value);
        if (match.Success)
        {
            return match.Value.ToLowerInvariant();
        }

        var base32 = Base32HashPattern().Match(value);
        return base32.Success ? DecodeBase32(base32.Value) : null;
    }

    private static string? DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var buffer = 0;
        var bits = 0;
        var bytes = new List<byte>(20);
        foreach (var character in value.ToUpperInvariant())
        {
            var index = alphabet.IndexOf(character, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return bytes.Count == 20 ? Convert.ToHexString(bytes.ToArray()).ToLowerInvariant() : null;
    }

    private static int? ParseInteger(string? value) =>
        int.TryParse(IntegerPattern().Match(value ?? string.Empty).Value, out var parsed)
            ? parsed
            : null;

    private static Uri? ParseUri(string? value, Uri baseUri, bool allowMagnet)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Relative, out var relativeValue))
        {
            return Uri.TryCreate(baseUri, relativeValue, out var resolved) ? resolved : null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme is "http" or "https" || (allowMagnet && absolute.Scheme == "magnet")
                ? absolute
                : null;
        }

        return null;
    }

    private static string? MagnetInfoHash(Uri? magnet)
    {
        if (magnet?.Scheme != "magnet")
        {
            return null;
        }

        var match = Regex.Match(
            Uri.UnescapeDataString(magnet.OriginalString),
            @"(?:^|[?&])xt=urn:btih:([A-Za-z0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsTorrentDownload(Uri? uri) =>
        uri is not null
        && uri.Scheme is "http" or "https"
        && uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    private static string? Get(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    [GeneratedRegex(@"(?<![A-Fa-f0-9])[A-Fa-f0-9]{40}(?![A-Fa-f0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex HexHashPattern();

    [GeneratedRegex(@"(?<![A-Z2-7])[A-Z2-7]{32}(?![A-Z2-7])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Base32HashPattern();

    [GeneratedRegex(@"[-+]?([0-9]*[.,])?[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?I?B?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}
