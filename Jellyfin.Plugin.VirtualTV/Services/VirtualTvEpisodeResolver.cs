using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Resolves the single library episode that must be opened for a watched-dependent
/// Virtual TV series block. The schedule selects the series; this resolver selects
/// the concrete episode for the active Jellyfin user.
/// </summary>
public sealed class VirtualTvEpisodeResolver
{
    private readonly VirtualTvContentCatalog _catalog;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public VirtualTvEpisodeResolver(
        VirtualTvContentCatalog catalog,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _catalog = catalog;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    public EpisodeResolution ResolveEpisode(
        Guid userId,
        ChannelConfiguration channel,
        Guid seriesId,
        Guid? lastItemId)
    {
        if (userId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Virtual TV cannot resolve a watched-dependent episode without an active Jellyfin user.");
        }

        var user = _userManager.GetUserById(userId)
            ?? throw new InvalidOperationException(
                "Virtual TV could not resolve the active Jellyfin user for watched-dependent playback.");

        var series = _catalog.GetSeries(channel)
            .FirstOrDefault(item => item.Id == seriesId)
            ?? throw new InvalidOperationException(
                "The scheduled series has no eligible playable episodes.");

        var candidates = series.Episodes
            .Select(item =>
            {
                var data = _userDataManager.GetUserData(user, item);
                return new Candidate(
                    item,
                    data?.Played == true,
                    Math.Max(0, data?.PlaybackPositionTicks ?? 0));
            })
            .ToList();

        Candidate selected;
        string selectionReason;

        if (string.Equals(channel.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase))
        {
            var unwatched = candidates
                .Where(candidate => !candidate.Played)
                .ToList();

            if (unwatched.Count > 0)
            {
                selected = PickRandom(unwatched, lastItemId);
                selectionReason = "RandomUnwatched";
            }
            else
            {
                selected = PickRandom(candidates, lastItemId);
                selectionReason = "RandomFallback";
            }
        }
        else
        {
            // Coverage-first chronological. The catalogue already applied season/Specials
            // eligibility and chronological ordering.
            var next = candidates.FirstOrDefault(candidate => !candidate.Played);

            if (next is not null)
            {
                selected = next;
                selectionReason = "NextUnwatched";
            }
            else
            {
                selected = PickRandom(candidates, lastItemId);
                selectionReason = "RandomFallback";
            }
        }

        // 1.9.1 was explicitly approved with watched-dependent series opening at 00:00.
        // PreviousPlaybackPositionTicks is retained for logging/diagnostics only.
        return new EpisodeResolution(
            selected.Item.Id,
            selected.PlaybackPositionTicks,
            selectionReason);
    }

    private static Candidate PickRandom(
        IReadOnlyList<Candidate> candidates,
        Guid? lastItemId)
    {
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No eligible episode is available.");
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var selectable = lastItemId.HasValue
            ? candidates.Where(candidate => candidate.Item.Id != lastItemId.Value).ToArray()
            : candidates.ToArray();

        if (selectable.Length == 0)
        {
            selectable = candidates.ToArray();
        }

        return selectable[Random.Shared.Next(selectable.Length)];
    }

    private sealed record Candidate(
        BaseItem Item,
        bool Played,
        long PlaybackPositionTicks);

    public sealed record EpisodeResolution(
        Guid ItemId,
        long PreviousPlaybackPositionTicks,
        string SelectionReason);
}
