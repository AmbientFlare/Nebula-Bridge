using Jellyfin.Data.Enums;
using MediaBrowser.Model.MediaInfo;
using Jellyfin.Database.Implementations.Entities; // User
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;

namespace NebulaBridge.Decorators;

public sealed class DtoServiceDecorator(IDtoService inner, Lazy<NebulaBridgeManager> manager, IHttpContextAccessor http)
    : IDtoService
{
    private readonly Lazy<NebulaBridgeManager> _manager = manager;
    private readonly IHttpContextAccessor _http = http;

    public double? GetPrimaryImageAspectRatio(BaseItem item) =>
        inner.GetPrimaryImageAspectRatio(item);

    public BaseItemDto GetBaseItemDto(
        BaseItem item,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        var dto = inner.GetBaseItemDto(item, options, user, owner);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
        IReadOnlyList<BaseItem> items,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        if (items.Count > 0 && items.All(item => item.GetBaseItemKind() == BaseItemKind.BoxSet))
        {
            // Copy before changing it: the caller owns this DtoOptions and may
            // reuse it for later calls in the same request.
            options = ShallowCopy(options);
            options.EnableUserData = false;
        }

        var list = inner.GetBaseItemDtos(items, options, user, owner);
        var itemsById = items
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var isApiListing = _http.HttpContext?.IsApiListing() == true;
        foreach (var itemDto in list)
        {
            itemsById.TryGetValue(itemDto.Id, out var item);
            Patch(itemDto, item, isApiListing, user);
        }
        return list;
    }

    public BaseItemDto GetItemByNameDto(
        BaseItem item,
        DtoOptions options,
        List<BaseItem>? taggedItems,
        User? user = null
    )
    {
        var dto = inner.GetItemByNameDto(item, options, taggedItems, user);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    internal static DtoOptions ShallowCopy(DtoOptions options) =>
        new()
        {
            Fields = options.Fields,
            ImageTypes = options.ImageTypes,
            ImageTypeLimit = options.ImageTypeLimit,
            EnableImages = options.EnableImages,
            EnableUserData = options.EnableUserData,
            AddCurrentProgram = options.AddCurrentProgram,
        };

    static bool IsNebulaBridge(BaseItemDto dto)
    {
        return dto.LocationType == LocationType.Remote
            && (
                dto.Type == BaseItemKind.Movie
                || dto.Type == BaseItemKind.Episode
                || dto.Type == BaseItemKind.Series
                || dto.Type == BaseItemKind.Season
            );
    }

    private void Patch(BaseItemDto dto, BaseItem? item, bool isList, User? user)
    {
        var manager = _manager.Value;
        if (item is not null && user is not null && IsNebulaBridge(dto) && manager.CanDelete(item, user))
        {
            dto.CanDelete = true;
        }

        if (IsNebulaBridge(dto))
        {
            if (dto.Path is not null && dto.Path.IsUrl())
            {
                // dto.Path = "/stub";


            }

            dto.CanDownload = true;
            // mark if placeholder
            if (
                isList
                || dto.MediaSources?.Length != 1
                || dto.Path is null
                // A deferred placeholder carries a null path on purpose, so that
                // ffprobe never sees the internal scheme. A null path is not the
                // internal stub path, so it takes the same branch as any other
                // non-stub source rather than dereferencing null.
                || !(
                    dto.MediaSources[0]
                        .Path?.StartsWith("nebulabridge", StringComparison.OrdinalIgnoreCase)
                    ?? false
                )
            )
            {
                if (dto.MediaSources != null)
                {
                    foreach (var source in dto.MediaSources)
                    {
                        //source.Path = "/stub";
                        //source.IsRemote = false;
                        // source.Protocol = MediaProtocol.File;
                    }
                }
                return;
            }

            dto.LocationType = LocationType.Virtual;
            dto.Path = null;
            dto.CanDownload = false;
        }
    }
}
