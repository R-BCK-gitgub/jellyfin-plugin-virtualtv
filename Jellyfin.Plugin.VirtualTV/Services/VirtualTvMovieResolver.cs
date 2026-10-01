using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Resolves materialized Personalized TV Movie Random Unwatched blocks against
/// current Jellyfin watched/resume state.
/// </summary>
public sealed class VirtualTvMovieResolver
{
    private readonly VirtualTvContentCatalog _catalog;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public VirtualTvMovieResolver(
        VirtualTvContentCatalog catalog,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _catalog = catalog;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    public MovieResolution Resolve(
        Guid userId,
        ChannelConfiguration channel,
        Guid? scheduledItemId,
        Guid? lastItemId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            throw new InvalidOperationException(
                "Movie Random Unwatched requires an active Jellyfin user.");
        }

        var items = _catalog.GetMovies(channel);
        if (items.Count == 0)
        {
            throw new InvalidOperationException("The movie channel has no eligible content.");
        }

        var candidates = items.Select(item =>
        {
            var data = _userDataManager.GetUserData(user, item);
            return new Candidate(
                item,
                data?.Played == true,
                Math.Max(0, data?.PlaybackPositionTicks ?? 0));
        }).ToList();

        var resumable = candidates
            .Where(item => !item.Played && item.PlaybackPositionTicks > 0)
            .ToList();
        if (resumable.Count > 0)
        {
            var resume = PickRandom(resumable, lastItemId);
            return new MovieResolution(
                resume.Item.Id,
                resume.PlaybackPositionTicks,
                !scheduledItemId.HasValue || resume.Item.Id != scheduledItemId.Value,
                "RandomResume");
        }

        var neverStarted = candidates
            .Where(item => !item.Played && item.PlaybackPositionTicks <= 0)
            .ToList();

        if (scheduledItemId.HasValue
            && (!lastItemId.HasValue || scheduledItemId.Value != lastItemId.Value))
        {
            var scheduled = neverStarted.FirstOrDefault(item => item.Item.Id == scheduledItemId.Value);
            if (scheduled is not null)
            {
                return new MovieResolution(
                    scheduled.Item.Id,
                    0,
                    false,
                    "MaterializedUnwatched");
            }
        }

        if (neverStarted.Count > 0)
        {
            var replacement = PickRandom(neverStarted, lastItemId);
            return new MovieResolution(
                replacement.Item.Id,
                0,
                scheduledItemId.HasValue,
                "RuntimeUnwatchedReplacement");
        }

        var fallback = PickRandom(candidates, lastItemId);
        return new MovieResolution(
            fallback.Item.Id,
            0,
            scheduledItemId.HasValue && fallback.Item.Id != scheduledItemId.Value,
            "RandomFallback");
    }

    public IReadOnlyList<BaseItem> GetUnwatched(Guid userId, ChannelConfiguration channel)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return [];
        }

        return _catalog.GetMovies(channel)
            .Where(item => _userDataManager.GetUserData(user, item)?.Played != true)
            .ToArray();
    }

    private static Candidate PickRandom(IReadOnlyList<Candidate> source, Guid? lastItemId)
    {
        if (source.Count == 0)
        {
            throw new InvalidOperationException("No eligible movie is available.");
        }

        if (source.Count == 1)
        {
            return source[0];
        }

        var candidates = lastItemId.HasValue
            ? source.Where(item => item.Item.Id != lastItemId.Value).ToArray()
            : source.ToArray();

        if (candidates.Length == 0)
        {
            candidates = source.ToArray();
        }

        return candidates[Random.Shared.Next(candidates.Length)];
    }

    private sealed record Candidate(BaseItem Item, bool Played, long PlaybackPositionTicks);

    public sealed record MovieResolution(
        Guid ItemId,
        long StartPositionTicks,
        bool ReplacedScheduledItem,
        string SelectionReason);
}
