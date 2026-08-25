using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Filters;

/// <summary>
/// Jellyfin normally prioritizes series activity in Next Up. For the top-level home row,
/// Nebula Bridge instead presents the newest aired next episode first so a recently aired
/// episode does not sit behind a series that has been dormant for years.
/// </summary>
public sealed class NextUpSortActionFilter(ILogger<NextUpSortActionFilter> logger)
    : IAsyncActionFilter,
        IOrderedFilter
{
    public int Order => 4;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next
    )
    {
        var action = context.GetActionName();
        if (
            action is not ("GetNextUp" or "NextUp")
            || HasScopedGuid(context, "seriesId")
            || HasScopedGuid(context, "parentId")
        )
        {
            await next().ConfigureAwait(false);
            return;
        }

        var requestedStart = ReadNullableInt(context, "startIndex") ?? 0;
        var requestedLimit = ReadNullableInt(context, "limit");

        // Jellyfin pages before returning the DTOs. Request the complete eligible set so the
        // newest episode is selected globally, then restore the caller's requested page.
        context.ActionArguments["startIndex"] = null;
        context.ActionArguments["limit"] = null;

        var executed = await next().ConfigureAwait(false);
        if (
            executed.Result is not ObjectResult
            {
                Value: QueryResult<BaseItemDto> result,
            }
        )
        {
            return;
        }

        var sorted = SortNewestFirst(result.Items)
            .Skip(Math.Max(0, requestedStart));
        if (requestedLimit is > 0)
        {
            sorted = sorted.Take(requestedLimit.Value);
        }

        result.Items = sorted.ToArray();
        logger.LogDebug(
            "Sorted {Count} top-level Next Up episodes by newest premiere date",
            result.Items.Count
        );
    }

    internal static IEnumerable<BaseItemDto> SortNewestFirst(
        IEnumerable<BaseItemDto> items,
        DateTime? utcNow = null
    ) =>
        items
            .Where(item =>
                !item.PremiereDate.HasValue
                || item.PremiereDate.Value <= (utcNow ?? DateTime.UtcNow)
            )
            .OrderByDescending(item => item.PremiereDate ?? DateTime.MinValue)
            .ThenByDescending(item => item.DateCreated)
            .ThenBy(item => item.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ParentIndexNumber)
            .ThenBy(item => item.IndexNumber);

    private static bool HasScopedGuid(ActionExecutingContext context, string key) =>
        context.ActionArguments.TryGetValue(key, out var value)
        && value switch
        {
            Guid guid => guid != Guid.Empty,
            string text => Guid.TryParse(text, out var guid) && guid != Guid.Empty,
            _ => false,
        };

    private static int? ReadNullableInt(ActionExecutingContext context, string key) =>
        context.ActionArguments.TryGetValue(key, out var value)
            ? value switch
            {
                int number => number,
                string text when int.TryParse(text, out var number) => number,
                _ => null,
            }
            : null;
}
