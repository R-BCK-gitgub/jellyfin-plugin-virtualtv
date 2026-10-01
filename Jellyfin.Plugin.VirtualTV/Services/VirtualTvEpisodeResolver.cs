using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Resolves the single library episode that must be opened for a watched-dependent
/// Personalized TV series block. Jellyfin watched/resume data is the source of truth.
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

        // The catalogue already supplies chronological episode order. Do not invent our own
        // watched threshold: Played and PlaybackPositionTicks are Jellyfin's persisted state.
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

        var resumable = candidates
            .Where(candidate => !candidate.Played && candidate.PlaybackPositionTicks > 0)
            .ToList();
        var neverStarted = candidates
            .Where(candidate => !candidate.Played && candidate.PlaybackPositionTicks <= 0)
            .ToList();

        Candidate selected;
        long startTicks;
        string selectionReason;

        if (string.Equals(channel.ContentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase))
        {
            if (resumable.Count > 0)
            {
                // Latest requirement: when several episodes are partially watched, Random
                // Unwatched chooses randomly among those Resume candidates before touching
                // any never-started episode.
                selected = PickRandom(resumable, lastItemId);
                startTicks = selected.PlaybackPositionTicks;
                selectionReason = "RandomResume";
            }
            else if (neverStarted.Count > 0)
            {
                selected = PickRandom(neverStarted, lastItemId);
                startTicks = 0;
                selectionReason = "RandomUnwatched";
            }
            else
            {
                selected = PickRandom(candidates, lastItemId);
                startTicks = 0;
                selectionReason = "RandomFallback";
            }
        }
        else
        {
            if (resumable.Count > 0)
            {
                // Next Unwatched is chronological: resume the earliest eligible partial
                // episode before selecting a never-started episode.
                selected = resumable[0];
                startTicks = selected.PlaybackPositionTicks;
                selectionReason = "NextResume";
            }
            else if (neverStarted.Count > 0)
            {
                selected = neverStarted[0];
                startTicks = 0;
                selectionReason = "NextUnwatched";
            }
            else
            {
                selected = PickRandom(candidates, lastItemId);
                startTicks = 0;
                selectionReason = "RandomFallback";
            }
        }

        return new EpisodeResolution(
            selected.Item.Id,
            startTicks,
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
        long StartPositionTicks,
        string SelectionReason);
}
