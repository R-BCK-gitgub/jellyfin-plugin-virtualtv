using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Applies Virtual TV per-user visibility through Jellyfin's native BlockedChannels policy.
/// Only the plugin's own channel ids are added/removed; unrelated user policy entries are kept.
/// </summary>
public sealed class VirtualTvVisibilityManager
{
    private const string GuideCssStart = "/* Virtual TV Guide UI START */";
    private const string GuideCssEnd = "/* Virtual TV Guide UI END */";

    private readonly ILiveTvManager _liveTvManager;
    private readonly IUserManager _userManager;
    private readonly VirtualTvUserContextService _userContext;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<VirtualTvVisibilityManager> _logger;

    public VirtualTvVisibilityManager(
        ILiveTvManager liveTvManager,
        IUserManager userManager,
        VirtualTvUserContextService userContext,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<VirtualTvVisibilityManager> logger)
    {
        _liveTvManager = liveTvManager;
        _userManager = userManager;
        _userContext = userContext;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        EnsureGuidePresentation();

        var internalChannels = _liveTvManager.GetInternalChannels(
            new LiveTvChannelQuery(),
            new DtoOptions(),
            cancellationToken)
            .Items
            .OfType<LiveTvChannel>()
            .Where(item => string.Equals(item.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var map = internalChannels
            .Where(item => VirtualTvLiveTvService.TryGetConfigurationChannelId(item.ExternalId, out _))
            .ToDictionary(
                item =>
                {
                    VirtualTvLiveTvService.TryGetConfigurationChannelId(item.ExternalId, out var id);
                    return id;
                },
                item => item,
                StringComparer.OrdinalIgnoreCase);

        // Stock Jellyfin places LiveTvChannel.Overview below the long Guide listing and the
        // server-side plugin API has no stable cross-client hook to relocate that field beneath
        // the title. v1.10.2 follows the product fallback: keep Description in plugin config,
        // but do not publish it as the channel Overview unless a clean client-independent
        // presentation mechanism is available in a future Jellyfin release.
        foreach (var channel in plugin.Configuration.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!map.TryGetValue(channel.Id, out var internalChannel))
            {
                continue;
            }

            var desiredOverview = string.Empty;
            if (!string.Equals(internalChannel.Overview ?? string.Empty, desiredOverview, StringComparison.Ordinal))
            {
                internalChannel.Overview = desiredOverview;
                await internalChannel.UpdateToRepositoryAsync(
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogDebug(
                    "Virtual TV cleared public Overview for channel {ChannelName}; configured description remains stored only in plugin settings.",
                    channel.Name);
            }
        }

        plugin.Configuration.KnownInternalChannelIds ??= [];
        var previousManagedIds = plugin.Configuration.KnownInternalChannelIds
            .Select(raw => Guid.TryParse(raw, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var currentManagedIds = map.Values.Select(item => item.Id).ToHashSet();
        var staleManagedIds = previousManagedIds
            .Where(id => !currentManagedIds.Contains(id))
            .ToArray();

        foreach (var user in _userManager.GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dto = _userManager.GetUserDto(user);
            var policy = dto.Policy;
            if (policy is null)
            {
                continue;
            }

            var blocked = (policy.BlockedChannels ?? []).ToHashSet();
            var changed = false;

            foreach (var channel in plugin.Configuration.Channels)
            {
                if (!map.TryGetValue(channel.Id, out var internalChannel))
                {
                    continue;
                }

                var internalId = internalChannel.Id;
                var shouldSee = IsVisibleToUser(channel, user.Id);
                if (shouldSee)
                {
                    if (blocked.Remove(internalId))
                    {
                        changed = true;
                    }
                }
                else if (blocked.Add(internalId))
                {
                    changed = true;
                }
            }

            foreach (var stale in staleManagedIds)
            {
                if (blocked.Remove(stale))
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            policy.BlockedChannels = blocked.ToArray();
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }

        plugin.Configuration.KnownInternalChannelIds = currentManagedIds
            .Select(id => id.ToString("N"))
            .ToList();
        plugin.SaveConfiguration();
    }

    private void EnsureGuidePresentation()
    {
        try
        {
            var branding = (BrandingOptions)_serverConfigurationManager.GetConfiguration("branding");
            var existing = branding.CustomCss ?? string.Empty;

            var start = existing.IndexOf(GuideCssStart, StringComparison.Ordinal);
            var end = existing.IndexOf(GuideCssEnd, StringComparison.Ordinal);
            if (start >= 0 && end >= start)
            {
                end += GuideCssEnd.Length;
                existing = (existing[..start] + existing[end..]).TrimEnd();
            }

            var block = """
/* Virtual TV Guide UI START */
.guide-channelHeaderCell,
.channelPrograms {
    min-height: 6.1em !important;
    height: 6.1em !important;
}
.guide-channelHeaderCell-tv,
.channelPrograms-tv {
    min-height: 5.2em !important;
    height: 5.2em !important;
}
.guideChannelImage {
    top: 9% !important;
    bottom: 9% !important;
    width: 48% !important;
}
.guideChannelNumber {
    max-width: 36% !important;
    padding-left: .7em !important;
    font-weight: 600 !important;
}
.guideChannelName {
    max-width: 62% !important;
    font-weight: 600 !important;
}
@media all and (min-width: 50em) {
    .channelsContainer,
    .guide-channelTimeslotHeader {
        width: 18vw !important;
    }
}
@media all and (min-width: 80em) {
    .channelsContainer,
    .guide-channelTimeslotHeader {
        width: 18vw !important;
    }
}
/* Virtual TV Guide UI END */
""";

            var desired = string.IsNullOrWhiteSpace(existing)
                ? block
                : existing + Environment.NewLine + Environment.NewLine + block;

            if (!string.Equals(branding.CustomCss ?? string.Empty, desired, StringComparison.Ordinal))
            {
                branding.CustomCss = desired;
                _serverConfigurationManager.SaveConfiguration("branding", branding);
                _logger.LogInformation("Virtual TV installed the enlarged Live TV Guide presentation CSS.");
            }
        }
        catch (Exception ex)
        {
            // Presentation is best-effort and must never block channel/visibility maintenance.
            _logger.LogWarning(ex, "Virtual TV could not install the optional Live TV Guide presentation CSS.");
        }
    }

    public bool IsVisibleToUser(ChannelConfiguration channel, Guid userId)
    {
        if (VirtualTvModePolicy.IsDynamicUnwatched(channel.ContentMode))
        {
            var ownerId = _userContext.ResolveOwnerUserId(channel);
            return ownerId != Guid.Empty && ownerId == userId;
        }

        if (channel.VisibleToAllUsers)
        {
            return true;
        }

        return channel.VisibleUserIds.Any(raw =>
            Guid.TryParse(raw, out var configured) && configured == userId);
    }
}
