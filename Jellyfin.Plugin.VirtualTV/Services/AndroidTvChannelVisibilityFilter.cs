using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Removes Personalized TV channels marked HideFromAndroidTv from responses sent specifically
/// to Jellyfin for Android TV. The underlying Jellyfin Live TV catalogue remains global and
/// unchanged, so Web, webOS and Jellyfin for Android phone/tablet continue to receive the channel.
/// </summary>
public sealed class AndroidTvChannelVisibilityFilter : IAsyncResultFilter
{
    private const string AndroidTvClient = "Jellyfin for Android TV";

    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AndroidTvChannelVisibilityFilter> _logger;

    public AndroidTvChannelVisibilityFilter(
        IAuthorizationContext authorizationContext,
        ILibraryManager libraryManager,
        ILogger<AndroidTvChannelVisibilityFilter> logger)
    {
        _authorizationContext = authorizationContext;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (!IsLiveTvResponse(path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        try
        {
            var auth = await _authorizationContext
                .GetAuthorizationInfo(context.HttpContext)
                .ConfigureAwait(false);

            if (!string.Equals(auth.Client, AndroidTvClient, StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Virtual TV could not resolve the client while applying Android TV channel visibility.");
            await next().ConfigureAwait(false);
            return;
        }

        if (context.Result is ObjectResult { Value: QueryResult<BaseItemDto> query })
        {
            if (IsChannelList(path))
            {
                var originalCount = query.Items.Count;
                var filtered = query.Items
                    .Where(item => !ShouldHideChannel(item.Id))
                    .ToArray();

                query.Items = filtered;
                query.TotalRecordCount = Math.Max(0, query.TotalRecordCount - (originalCount - filtered.Length));
            }
            else if (IsProgramList(path))
            {
                var originalCount = query.Items.Count;
                var filtered = query.Items
                    .Where(item => !item.ChannelId.HasValue || !ShouldHideChannel(item.ChannelId.Value))
                    .ToArray();

                query.Items = filtered;
                query.TotalRecordCount = Math.Max(0, query.TotalRecordCount - (originalCount - filtered.Length));
            }
        }
        else if (context.Result is ObjectResult { Value: BaseItemDto item }
                 && IsChannelDetail(path)
                 && ShouldHideChannel(item.Id))
        {
            context.Result = new NotFoundResult();
        }

        await next().ConfigureAwait(false);
    }

    private bool ShouldHideChannel(Guid internalChannelId)
    {
        var liveChannel = _libraryManager.GetItemById(internalChannelId) as LiveTvChannel;
        if (liveChannel is null
            || !string.Equals(liveChannel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase)
            || !VirtualTvLiveTvService.TryGetConfigurationChannelId(liveChannel.ExternalId, out var configurationChannelId))
        {
            return false;
        }

        var channel = Plugin.Instance?.Configuration.Channels.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, configurationChannelId, StringComparison.OrdinalIgnoreCase));

        return channel is not null
            && channel.HideFromAndroidTv
            && !VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience);
    }

    private static bool IsLiveTvResponse(string path)
        => IsChannelList(path) || IsChannelDetail(path) || IsProgramList(path);

    private static bool IsChannelList(string path)
        => path.EndsWith("/LiveTv/Channels", StringComparison.OrdinalIgnoreCase);

    private static bool IsChannelDetail(string path)
        => path.Contains("/LiveTv/Channels/", StringComparison.OrdinalIgnoreCase);

    private static bool IsProgramList(string path)
        => path.Contains("/LiveTv/Programs", StringComparison.OrdinalIgnoreCase);
}
