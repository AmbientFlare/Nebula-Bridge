using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NebulaBridge.Services;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/acquisition-cache")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AcquisitionCacheController(
    AcquisitionCoordinator acquisitions,
    ILibraryManager libraryManager) : ControllerBase
{
    [HttpGet]
    public async Task<AcquisitionCacheReviewResponse> List(CancellationToken ct) =>
        new(
            acquisitions.Enabled,
            (await acquisitions.ListAsync(ct).ConfigureAwait(false))
                .Where(IsActiveReviewJob)
                .OrderByDescending(job => job.LastAccessUtc ?? job.UpdatedUtc)
                .Select(ToReview)
                .ToArray());

    [HttpGet("kept")]
    public async Task<AcquisitionKeptMediaResponse> ListKept(CancellationToken ct)
    {
        var jobs = await acquisitions.ListAsync(ct).ConfigureAwait(false);
        var imported = jobs
            .Where(job => job.Imported)
            .OrderByDescending(job => job.LastAccessUtc ?? job.UpdatedUtc)
            .Take(100)
            .Select(ToReview)
            .ToArray();
        var seriesPolicies = jobs
            .Where(job => job.Retention == AcquisitionRetention.KeepSeries
                && !string.IsNullOrWhiteSpace(job.SeriesId))
            .GroupBy(job => job.SeriesId!, StringComparer.Ordinal)
            .Select(group => new AcquisitionSeriesPolicyReview(
                group.Key,
                ResolveSeriesTitle(group),
                group.Count(job => job.Imported),
                group.Count(job => !job.Imported),
                group.Max(job => job.LastAccessUtc ?? job.UpdatedUtc)))
            .OrderByDescending(policy => policy.LastActivity)
            .ToArray();
        return new AcquisitionKeptMediaResponse(imported, seriesPolicies);
    }

    [HttpPost("{id:guid}/retain/{retention}")]
    public async Task<IActionResult> Retain(Guid id, AcquisitionRetention retention, CancellationToken ct)
    {
        if (!acquisitions.Enabled)
            return Conflict("Local acquisition is disabled.");
        return await acquisitions.RetainAsync(id, retention, ct).ConfigureAwait(false)
            ? NoContent()
            : NotFound();
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (!acquisitions.Enabled)
            return Conflict("Local acquisition is disabled.");
        return await acquisitions.RetryAsync(id, ct).ConfigureAwait(false)
            ? Accepted()
            : Conflict("The job is not a recoverable retained failure.");
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!acquisitions.Enabled)
            return Conflict("Local acquisition is disabled.");
        return await acquisitions.CancelAsync(id, ct).ConfigureAwait(false)
            ? NoContent()
            : Conflict("The job is not a retained completion that can be cancelled.");
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Purge(Guid id, CancellationToken ct) =>
        await acquisitions.PurgeAsync(id, ct).ConfigureAwait(false) ? NoContent() : Conflict();

    internal static bool IsActiveReviewJob(AcquisitionJob job) => !job.Imported;

    private AcquisitionJobReview ToReview(AcquisitionJob job) =>
        AcquisitionJobReview.From(job, ResolveTitle(job));

    private string ResolveSeriesTitle(IGrouping<string, AcquisitionJob> group) =>
        group.Select(ResolveSeriesName).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
        ?? group.Key;

    private string ResolveTitle(AcquisitionJob job)
    {
        var item = FindItem(job);
        return item switch
        {
            Episode episode => FormatEpisodeTitle(episode),
            BaseItem { Name: { Length: > 0 } name } => name,
            _ => job.FileName,
        };
    }

    private string ResolveSeriesName(AcquisitionJob job) => FindItem(job) is Episode
    { SeriesName: { Length: > 0 } seriesName } ? seriesName : ResolveTitle(job);

    private BaseItem? FindItem(AcquisitionJob job) =>
        libraryManager.GetItemById(job.LocalItemId ?? job.ItemId)
        ?? FindLocalItemAtFinalPath(job.FinalPath);

    internal static string FormatEpisodeTitle(Episode episode)
    {
        var series = string.IsNullOrWhiteSpace(episode.SeriesName) ? "Series" : episode.SeriesName;
        var code = episode.ParentIndexNumber is { } season && episode.IndexNumber is { } number
            ? $"S{season:00}E{number:00}"
            : null;
        return string.IsNullOrWhiteSpace(code)
            ? $"{series} — {episode.Name ?? "Episode"}"
            : $"{series} — {code} {episode.Name ?? "Episode"}";
    }

    private BaseItem? FindLocalItemAtFinalPath(string? finalPath)
    {
        if (string.IsNullOrWhiteSpace(finalPath))
            return null;
        return libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = finalPath,
            IsDeadPerson = true,
        }).FirstOrDefault(item => string.Equals(
            item.Path,
            finalPath,
            StringComparison.Ordinal));
    }
}

public sealed record AcquisitionCacheReviewResponse(
    bool Enabled,
    IReadOnlyList<AcquisitionJobReview> Jobs);

public sealed record AcquisitionKeptMediaResponse(
    IReadOnlyList<AcquisitionJobReview> Imported,
    IReadOnlyList<AcquisitionSeriesPolicyReview> SeriesPolicies);

public sealed record AcquisitionSeriesPolicyReview(
    string SeriesId,
    string Title,
    int ImportedEpisodes,
    int PendingEpisodes,
    DateTimeOffset LastActivity);

public sealed record AcquisitionJobReview(
    Guid Id,
    Guid ItemId,
    string Title,
    string MediaType,
    string? SeriesId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string State,
    string Retention,
    long CachedBytes,
    long? ExpectedBytes,
    double? Percent,
    DateTimeOffset? LastUsefulAccess,
    DateTimeOffset? ExpiresUtc,
    bool Imported,
    string? Failure,
    bool CanKeepItem,
    bool CanKeepSeries,
    bool CanPurge,
    bool CanRetry,
    bool CanCancel)
{
    public static AcquisitionJobReview From(AcquisitionJob job, string? title = null)
    {
        var cached = AcquisitionCoordinator.GetCachedBytes(job);
        var expected = job.ExpectedBytes;
        return new(
            job.Id,
            job.ItemId,
            title ?? job.FileName,
            string.IsNullOrWhiteSpace(job.SeriesId) ? "Movie" : "Episode",
            job.SeriesId,
            job.SeasonNumber,
            job.EpisodeNumber,
            job.State.ToString(),
            job.Retention.ToString(),
            cached,
            expected,
            expected is > 0 ? Math.Round(100d * cached / expected.Value, 1) : null,
            job.LastAccessUtc,
            job.ExpiresUtc,
            job.Imported,
            job.Error ?? job.ReconciliationError,
            !job.Imported && job.Retention == AcquisitionRetention.Temporary,
            !job.Imported && job.Retention == AcquisitionRetention.Temporary
                && !string.IsNullOrWhiteSpace(job.SeriesId),
            AcquisitionCoordinator.CanPurge(job),
            job.Retention != AcquisitionRetention.Temporary
                && job.State == AcquisitionJobState.Failed,
            AcquisitionCoordinator.CanCancel(job));
    }
}
