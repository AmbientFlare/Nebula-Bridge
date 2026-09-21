using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

/// <summary>Moves only fully covered staging files into administrator-configured ordinary media roots.</summary>
public sealed class AcquisitionImportService(
    AcquisitionJobStore store,
    ILibraryManager libraryManager,
    AcquisitionActivityTracker activity,
    ILogger<AcquisitionImportService> logger
)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationGates = new(StringComparer.Ordinal);

    public async Task<bool> ImportAsync(Guid jobId, CancellationToken ct)
    {
        await using var transition = await activity.AcquireExclusiveAsync(jobId, ct).ConfigureAwait(false);
        var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == jobId);
        if (job is null || job.State is not (AcquisitionJobState.ReadyToImport or AcquisitionJobState.Importing)
            || job.ExpectedBytes is not > 0 || !IsComplete(job)) return false;
        var item = libraryManager.GetItemById(job.ItemId);
        if (item is not Video video) return await FailAsync(job, "unknown_media", ct).ConfigureAwait(false);
        var sourceSeries = video is Episode episode
            ? libraryManager.GetItemById(episode.SeriesId) as Series
            : null;
        var destination = BuildDestination(video, job.FileName, sourceSeries);
        if (destination is null) return await FailAsync(job, "unsupported_media_identity", ct).ConfigureAwait(false);
        var root = GetImportRoot(video, NebulaBridgePlugin.Instance!.Configuration);
        if (string.IsNullOrWhiteSpace(root)) return await FailAsync(job, "destination_not_configured", ct).ConfigureAwait(false);
        var final = job.State == AcquisitionJobState.Importing && !string.IsNullOrWhiteSpace(job.FinalPath)
            ? EnsureUnderRoot(root, job.FinalPath)
            : EnsureUnderRoot(root, destination);
        if (final is null) return await FailAsync(job, "unsafe_destination", ct).ConfigureAwait(false);
        var source = Path.Combine(store.StagingPath, job.Id.ToString("N"), "media.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await using var destinationLease = await AcquireDestinationAsync(final, ct).ConfigureAwait(false);
        {
            var temporary = job.ImportTempPath;
            if (string.IsNullOrWhiteSpace(temporary))
                temporary = JobOwnedTemporaryPath(final, job.Id);
            else if (!string.Equals(temporary, JobOwnedTemporaryPath(final, job.Id), StringComparison.Ordinal))
                return await FailAsync(job, "unsafe_import_temporary_path", ct).ConfigureAwait(false);
            var contentHash = job.ContentSha256;
            var ownedArtifact = File.Exists(source) ? source : File.Exists(temporary) ? temporary : null;
            if (string.IsNullOrWhiteSpace(contentHash) && ownedArtifact is not null)
                contentHash = await ComputeSha256Async(ownedArtifact, ct).ConfigureAwait(false);
            // Persist paths and content identity before touching either file. The hash, rather than
            // path or length alone, is the durable ownership proof used by recovery.
            if (job.State != AcquisitionJobState.Importing || !string.Equals(job.FinalPath, final, StringComparison.Ordinal)
                || !string.Equals(job.ImportTempPath, temporary, StringComparison.Ordinal)
                || !string.Equals(job.ContentSha256, contentHash, StringComparison.Ordinal))
            {
                job = job with
                {
                    State = AcquisitionJobState.Importing,
                    FinalPath = final,
                    ImportTempPath = temporary,
                    ContentSha256 = contentHash,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
                await store.UpsertAsync(job, ct).ConfigureAwait(false);
            }
            if (File.Exists(final))
            {
                if (!await IsOwnedArtifactAsync(final, job.ExpectedBytes.Value, job.ContentSha256, ct).ConfigureAwait(false))
                    return await FailAsync(job, "destination_conflict", ct).ConfigureAwait(false);
                if (File.Exists(source)) File.Delete(source);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            else
            {
                if (!File.Exists(source) && !File.Exists(temporary))
                    return await FailAsync(job, "staging_missing_or_incomplete", ct).ConfigureAwait(false);
                if (File.Exists(temporary)
                    && !await IsOwnedArtifactAsync(temporary, job.ExpectedBytes.Value, job.ContentSha256, ct).ConfigureAwait(false))
                {
                    if (!File.Exists(source)) return await FailAsync(job, "finalize_owned_artifact_incomplete", ct).ConfigureAwait(false);
                    File.Delete(temporary);
                }
                if (!File.Exists(temporary))
                {
                    try { File.Move(source, temporary); }
                    catch (IOException) { File.Copy(source, temporary, overwrite: false); }
                }
                if (new FileInfo(temporary).Length != job.ExpectedBytes) return await FailAsync(job, "finalize_length_mismatch", ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(job.ContentSha256)
                    || !string.Equals(await ComputeSha256Async(temporary, ct).ConfigureAwait(false), job.ContentSha256, StringComparison.Ordinal))
                    return await FailAsync(job, "finalize_hash_mismatch", ct).ConfigureAwait(false);
                File.Move(temporary, final, overwrite: false);
                if (File.Exists(source)) File.Delete(source);
            }
            await store.UpsertAsync(job with
            {
                State = AcquisitionJobState.Imported,
                Imported = true,
                FinalPath = final,
                ImportTempPath = temporary,
                ReconciliationState = AcquisitionReconciliationState.Pending,
                ReconciliationError = null,
                Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "import-complete",
                job.Id,
                new Dictionary<string, object?>
                {
                    ["expectedBytes"] = job.ExpectedBytes,
                    ["mediaType"] = video is Episode ? "episode" : "movie",
                });
            libraryManager.QueueLibraryScan();
            return true;
        }
    }

    internal static bool IsComplete(AcquisitionJob job) => job.ExpectedBytes is { } length
        && (job.CachedRanges ?? []).Any(range => range.Contains(0, length - 1));

    internal static string GetImportRoot(Video video, PluginConfiguration configuration) =>
        video is Episode ? configuration.SeriesImportPath : configuration.MovieImportPath;

    internal static string JobOwnedTemporaryPath(string final, Guid jobId) => final + $".{jobId:N}.nebulabridge-importing";

    internal static async Task<IAsyncDisposable> AcquireDestinationAsync(string final, CancellationToken ct)
    {
        var gate = DestinationGates.GetOrAdd(final, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new DestinationLease(gate);
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    internal static async Task<bool> IsOwnedArtifactAsync(string path, long expectedLength, string? expectedHash, CancellationToken ct) =>
        File.Exists(path) && !string.IsNullOrWhiteSpace(expectedHash)
        && new FileInfo(path).Length == expectedLength
        && string.Equals(await ComputeSha256Async(path, ct).ConfigureAwait(false), expectedHash, StringComparison.Ordinal);

    internal static string SafeName(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character)
        || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
            ? '_'
            : character)).Trim(' ', '.');

    internal static string? BuildDestination(Video video, string providerFilename, Series? sourceSeries = null)
    {
        var extension = Path.GetExtension(providerFilename);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10) extension = ".mkv";
        if (video is Episode episode && episode.IndexNumber is { } episodeNumber && episode.ParentIndexNumber is { } seasonNumber)
        {
            var series = SafeName(episode.SeriesName ?? "Series");
            var tvdb = sourceSeries?.GetProviderId(MetadataProvider.Tvdb);
            var tmdb = sourceSeries?.GetProviderId(MetadataProvider.Tmdb);
            var providerTag = !string.IsNullOrWhiteSpace(tvdb)
                ? $" [tvdbid-{SafeName(tvdb)}]"
                : !string.IsNullOrWhiteSpace(tmdb)
                    ? $" [tmdbid-{SafeName(tmdb)}]"
                    : null;
            if (providerTag is null) return null;
            var seriesDirectory = series + providerTag;
            var episodeName = SafeName(episode.Name ?? "Episode");
            return Path.Combine(seriesDirectory, $"Season {seasonNumber:00}", $"{series} S{seasonNumber:00}E{episodeNumber:00} - {episodeName}{extension}");
        }
        if (video is MediaBrowser.Controller.Entities.Movies.Movie movie)
        {
            var title = SafeName(movie.Name ?? "Movie");
            var year = movie.ProductionYear is { } value ? $" ({value})" : string.Empty;
            var id = movie.GetProviderId(MetadataProvider.Tmdb) ?? movie.GetProviderId(MetadataProvider.Imdb);
            var tag = string.IsNullOrWhiteSpace(id) ? string.Empty : $" [{(movie.GetProviderId(MetadataProvider.Tmdb) is null ? "imdbid" : "tmdbid")}-{SafeName(id)}]";
            var baseName = title + year + tag;
            return Path.Combine(baseName, baseName + extension);
        }
        return null;
    }

    private static string? EnsureUnderRoot(string root, string relative)
    {
        var basePath = Path.GetFullPath(root);
        var final = Path.GetFullPath(Path.Combine(basePath, relative));
        return final.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? final : null;
    }

    private async Task<bool> FailAsync(AcquisitionJob job, string error, CancellationToken ct)
    {
        logger.LogWarning("Acquisition import failed for {JobId} ({Reason})", job.Id, error);
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "import-failed",
            job.Id,
            new Dictionary<string, object?> { ["reason"] = error });
        await store.UpsertAsync(job with { State = AcquisitionJobState.Failed, Error = error, UpdatedUtc = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        return false;
    }

    private sealed class DestinationLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
