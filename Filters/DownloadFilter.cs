using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NebulaBridge.Filters;

public sealed class DownloadFilter(
    ILibraryManager library,
    NebulaBridgeManager manager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    IHttpClientFactory httpClientFactory
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() != "GetDownload" || !ctx.TryGetRouteGuid(out var guid))
        {
            await next();
            return;
        }

        var user = ctx.TryGetUserId(out var userId) ? userManager.GetUserById(userId) : null;

        if (user is not null)
        {
            var mediaSourceIdStr = ctx.HttpContext.Items["MediaSourceId"] as string;
            var hasMediaSourceId = Guid.TryParse(mediaSourceIdStr, out var mediaSourceId);

            var item = library.GetItemById<Video>(hasMediaSourceId ? mediaSourceId : guid, user);

            if (item != null && manager.IsStremio(item))
            {
                var path = item.Path;

                // Some clients omit the media-source ID and use the item ID instead.
                if (!hasMediaSourceId || !item.IsStream())
                {
                    path = mediaSourceManager
                        .GetStaticMediaSources(item, true, user)
                        .FirstOrDefault()
                        ?.Path;
                }

                if (!TryGetHttpDownloadUri(path, out var downloadUri))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                var client = httpClientFactory.CreateClient();
                var cancellationToken = ctx.HttpContext.RequestAborted;

                var resp = await client.GetAsync(
                    downloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                ).ConfigureAwait(false);

                resp.EnsureSuccessStatusCode();

                ctx.HttpContext.Response.RegisterForDispose(resp);

                var stream = await resp.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                var contentType =
                    resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

                var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = Path.GetFileName(downloadUri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "download";
                }

                if (resp.Content.Headers.ContentLength is { } len)
                {
                    ctx.HttpContext.Response.ContentLength = len;
                }

                ctx.Result = new FileStreamResult(stream, contentType)
                {
                    FileDownloadName = fileName,
                    EnableRangeProcessing = true,
                };
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    internal static bool TryGetHttpDownloadUri(string? path, out Uri downloadUri)
    {
        if (
            Uri.TryCreate(path, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
        )
        {
            downloadUri = parsed;
            return true;
        }

        downloadUri = null!;
        return false;
    }
}
