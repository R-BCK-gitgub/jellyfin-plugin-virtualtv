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
    private const string LegacyGuideCssStart = "/* Virtual TV Guide UI START */";
    private const string LegacyGuideCssEnd = "/* Virtual TV Guide UI END */";
    private const string GuideTextCssStart = "/* Virtual TV Guide Text UI START */";
    private const string GuideTextCssEnd = "/* Virtual TV Guide Text UI END */";

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

        EnsureGuideTextPresentation();

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

    private void EnsureGuideTextPresentation()
    {
        try
        {
            var branding = (BrandingOptions)_serverConfigurationManager.GetConfiguration("branding");
            var existing = branding.CustomCss ?? string.Empty;

            // Remove the pre-1.10.16 geometry override, if it is still present, then replace
            // only our own text-polish block. Unrelated Jellyfin Branding CSS is preserved.
            var cleaned = RemoveCssBlock(existing, LegacyGuideCssStart, LegacyGuideCssEnd);
            cleaned = RemoveCssBlock(cleaned, GuideTextCssStart, GuideTextCssEnd);

            const string guideTextCss = """
            /* Virtual TV Guide Text UI START */
            /*
             * Text-only polish for the stock Jellyfin Live TV Guide.
             * Keep row/cell/channel dimensions, spacing, logo sizing and positioning untouched.
             */
            .guideProgramNameText {
                font-size: .82em !important;
                line-height: 1.15 !important;
                white-space: normal !important;
                overflow: hidden !important;
                text-overflow: ellipsis !important;
                display: -webkit-box !important;
                -webkit-box-orient: vertical !important;
                -webkit-line-clamp: 2 !important;
                min-width: 0 !important;
                word-break: normal !important;
                overflow-wrap: normal !important;
            }

            .guideChannelNumber,
            .guideChannelName {
                font-size: .80em !important;
                line-height: 1.15 !important;
            }
            /* Virtual TV Guide Text UI END */
            """;

            var desired = string.IsNullOrWhiteSpace(cleaned)
                ? guideTextCss
                : cleaned.TrimEnd() + Environment.NewLine + Environment.NewLine + guideTextCss;

            if (string.Equals(existing, desired, StringComparison.Ordinal))
            {
                return;
            }

            branding.CustomCss = desired;
            _serverConfigurationManager.SaveConfiguration("branding", branding);

            _logger.LogInformation(
                "Virtual TV applied text-only Live TV Guide readability styling without changing Guide geometry.");
        }
        catch (Exception ex)
        {
            // Presentation is best-effort and must never block channel/visibility maintenance.
            _logger.LogWarning(
                ex,
                "Virtual TV could not apply the text-only Live TV Guide readability styling.");
        }
    }

    private static string RemoveCssBlock(string css, string startMarker, string endMarker)
    {
        var result = css;

        while (true)
        {
            var start = result.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return result.Trim();
            }

            var end = result.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < start)
            {
                return result.Trim();
            }

            end += endMarker.Length;
            result = (result[..start] + result[end..]).Trim();
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
