using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
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
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public VirtualTvEpisodeResolver(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    public EpisodeResolution ResolveEpisode(
        Guid userId,
        Guid seriesId,
        string contentMode,
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

        var episodes = GetEpisodes(seriesId);
        if (episodes.Count == 0)
        {
            throw new InvalidOperationException("The scheduled series has no playable episodes.");
        }

        var candidates = episodes
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

        if (string.Equals(contentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase))
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
            // Next Unwatched is coverage-first chronological. A later partially watched
            // episode never jumps ahead of an earlier never-started episode. Played=false
            // already includes both never-started and partially watched episodes.
            var next = candidates
                .Where(candidate => !candidate.Played)
                .OrderBy(candidate => candidate.Item.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(candidate => candidate.Item.IndexNumber ?? int.MaxValue)
                .ThenBy(candidate => candidate.Item.PremiereDate ?? DateTime.MaxValue)
                .ThenBy(candidate => candidate.Item.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

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

        return new EpisodeResolution(
            selected.Item.Id,
            selected.PlaybackPositionTicks,
            selectionReason);
    }

    private List<BaseItem> GetEpisodes(Guid seriesId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            IsVirtualItem = false
        })
        // Season 0 / Specials stay excluded until the dedicated season/specials feature is
        // implemented. Normal episodes remain deterministically ordered by season/episode.
        .Where(item => item.ParentIndexNumber.GetValueOrDefault() != 0)
        .OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
        .ThenBy(item => item.IndexNumber ?? int.MaxValue)
        .ThenBy(item => item.PremiereDate ?? DateTime.MaxValue)
        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

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
