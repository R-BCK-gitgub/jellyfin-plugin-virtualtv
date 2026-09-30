using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
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
    private readonly ILiveTvManager _liveTvManager;
    private readonly IUserManager _userManager;
    private readonly VirtualTvUserContextService _userContext;
    private readonly ILogger<VirtualTvVisibilityManager> _logger;

    public VirtualTvVisibilityManager(
        ILiveTvManager liveTvManager,
        IUserManager userManager,
        VirtualTvUserContextService userContext,
        ILogger<VirtualTvVisibilityManager> logger)
    {
        _liveTvManager = liveTvManager;
        _userManager = userManager;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var plugin = global::Jellyfin.Plugin.VirtualTV.Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

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
                item => item.Id,
                StringComparer.OrdinalIgnoreCase);

        if (map.Count == 0)
        {
            return;
        }

        plugin.Configuration.KnownInternalChannelIds ??= [];
        var previousManagedIds = plugin.Configuration.KnownInternalChannelIds
            .Select(raw => Guid.TryParse(raw, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var currentManagedIds = map.Values.ToHashSet();
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
                if (!map.TryGetValue(channel.Id, out var internalId))
                {
                    continue;
                }

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

            // Remove stale blocks belonging to deleted Virtual TV channels while retaining
            // all non-Virtual-TV blocked channels.
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
