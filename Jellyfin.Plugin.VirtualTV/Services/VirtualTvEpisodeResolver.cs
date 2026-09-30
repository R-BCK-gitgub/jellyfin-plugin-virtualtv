using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Resolves user-specific episodes for Next Unwatched and Random Unwatched blocks.
/// The schedule selects the series; this resolver selects the actual episode.
/// </summary>
public sealed class VirtualTvEpisodeResolver
{
    private const int QueueLength = 8;

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

    public EpisodeResolution ResolveQueue(
        Guid userId,
        Guid seriesId,
        string contentMode,
        Guid? lastItemId)
    {
        var episodes = GetEpisodes(seriesId);
        if (episodes.Count == 0)
        {
            throw new InvalidOperationException("The scheduled series has no playable episodes.");
        }

        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        var states = episodes.Select(item =>
        {
            var data = user is null ? null : _userDataManager.GetUserData(user, item);
            return new Candidate(
                item,
                data?.Played == true,
                Math.Max(0, data?.PlaybackPositionTicks ?? 0));
        }).ToList();

        List<Candidate> ordered;
        if (string.Equals(contentMode, VirtualTvModePolicy.RandomUnwatched, StringComparison.OrdinalIgnoreCase))
        {
            ordered = BuildRandomUnwatched(states, lastItemId);
        }
        else
        {
            ordered = BuildNextUnwatched(states, lastItemId);
        }

        if (ordered.Count == 0)
        {
            ordered = BuildRandomFallback(states, lastItemId);
        }

        var first = ordered[0];
        var ids = ordered
            .Take(QueueLength)
            .Select(candidate => candidate.Item.Id)
            .Distinct()
            .ToArray();

        return new EpisodeResolution(
            first.Item.Id,
            first.PlaybackPositionTicks,
            ids);
    }

    public long GetResumePosition(Guid userId, Guid itemId)
    {
        if (userId == Guid.Empty)
        {
            return 0;
        }

        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || item is null)
        {
            return 0;
        }

        var data = _userDataManager.GetUserData(user, item);
        return data is { Played: false } ? Math.Max(0, data.PlaybackPositionTicks) : 0;
    }

    private List<BaseItem> GetEpisodes(Guid seriesId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            IsVirtualItem = false
        })
        .Where(item => item.ParentIndexNumber.GetValueOrDefault() != 0)
        .OrderBy(item => item.PremiereDate ?? DateTime.MaxValue)
        .ThenBy(item => item.ParentIndexNumber ?? int.MaxValue)
        .ThenBy(item => item.IndexNumber ?? int.MaxValue)
        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<Candidate> BuildNextUnwatched(
        IReadOnlyList<Candidate> candidates,
        Guid? lastItemId)
    {
        // Next Unwatched intentionally excludes episodes without an air date. This keeps
        // chronological progression deterministic; undated episodes remain available to Random.
        var eligible = candidates
            .Where(candidate => candidate.Item.PremiereDate.HasValue && !candidate.Played)
            .ToList();

        var partial = eligible
            .Where(candidate => candidate.PlaybackPositionTicks > 0)
            .OrderBy(candidate => candidate.Item.PremiereDate)
            .ThenBy(candidate => candidate.Item.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(candidate => candidate.Item.IndexNumber ?? int.MaxValue)
            .ToList();

        var fresh = eligible
            .Where(candidate => candidate.PlaybackPositionTicks == 0)
            .OrderBy(candidate => candidate.Item.PremiereDate)
            .ThenBy(candidate => candidate.Item.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(candidate => candidate.Item.IndexNumber ?? int.MaxValue)
            .ToList();

        var result = partial.Concat(fresh).ToList();
        AvoidImmediateRepeat(result, lastItemId);
        return result;
    }

    private static List<Candidate> BuildRandomUnwatched(
        IReadOnlyList<Candidate> candidates,
        Guid? lastItemId)
    {
        var result = candidates
            .Where(candidate => !candidate.Played)
            .ToList();

        Shuffle(result);
        AvoidImmediateRepeat(result, lastItemId);

        // If a partially watched episode is selected first, Jellyfin can resume it. Later
        // queue entries start at zero, so keep additional partial episodes out of the prebuilt
        // queue; they will be reconsidered the next time the block resolves.
        if (result.Count > 0 && result[0].PlaybackPositionTicks > 0)
        {
            var first = result[0];
            result = new[] { first }
                .Concat(result.Skip(1).Where(candidate => candidate.PlaybackPositionTicks == 0))
                .ToList();
        }

        return result;
    }

    private static List<Candidate> BuildRandomFallback(
        IReadOnlyList<Candidate> candidates,
        Guid? lastItemId)
    {
        var result = candidates.ToList();
        Shuffle(result);
        AvoidImmediateRepeat(result, lastItemId);
        return result;
    }

    private static void AvoidImmediateRepeat(List<Candidate> candidates, Guid? lastItemId)
    {
        if (!lastItemId.HasValue || candidates.Count < 2 || candidates[0].Item.Id != lastItemId.Value)
        {
            return;
        }

        var replacementIndex = candidates.FindIndex(1, candidate => candidate.Item.Id != lastItemId.Value);
        if (replacementIndex > 0)
        {
            (candidates[0], candidates[replacementIndex]) = (candidates[replacementIndex], candidates[0]);
        }
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private sealed record Candidate(BaseItem Item, bool Played, long PlaybackPositionTicks);

    public sealed record EpisodeResolution(
        Guid ItemId,
        long StartPositionTicks,
        Guid[] QueueItemIds);
}
