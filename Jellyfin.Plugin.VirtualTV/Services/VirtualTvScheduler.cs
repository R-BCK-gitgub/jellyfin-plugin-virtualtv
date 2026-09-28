using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Materializes and resolves Virtual TV schedules.
/// </summary>
public sealed class VirtualTvScheduler
{
    private static readonly TimeSpan FutureHorizon = TimeSpan.FromDays(30);
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(7);

    private readonly object _gate = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ScheduleStore _store;
    private readonly ILogger<VirtualTvScheduler> _logger;
    private readonly Dictionary<string, string> _runtimeLastRandom = new(StringComparer.OrdinalIgnoreCase);

    public VirtualTvScheduler(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ScheduleStore store,
        ILogger<VirtualTvScheduler> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _store = store;
        _logger = logger;
    }

    public ChannelScheduleState GetState(string channelId) => _store.Load(channelId);

    public void Delete(string channelId) => _store.Delete(channelId);

    public ChannelScheduleState Ensure(ChannelConfiguration channel, DateTime nowUtc)
    {
        lock (_gate)
        {
            var state = _store.Load(channel.Id);
            var needsInitial = state.Entries.Count == 0;
            var lastEnd = state.Entries
                .Select(e => ParseUtc(e.EndUtc))
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            if (needsInitial)
            {
                var anchor = GetInitialAnchorUtc(channel, nowUtc);
                state = new ChannelScheduleState { ChannelId = channel.Id };
                GenerateRange(channel, state, anchor, GetTargetEndUtc(channel, nowUtc), CancellationToken.None);
                _store.Save(state);
                PersistMutableChannelState();
                return state;
            }

            if (lastEnd < nowUtc || lastEnd < GetTargetEndUtc(channel, nowUtc))
            {
                ExtendInternal(channel, state, nowUtc, CancellationToken.None);
                _store.Save(state);
                PersistMutableChannelState();
            }

            return state;
        }
    }

    public ChannelScheduleState GenerateNew(ChannelConfiguration channel, DateTime nowUtc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var previous = _store.Load(channel.Id);
            var cutoff = GetRegenerationCutoff(channel, previous, nowUtc);
            var preserved = previous.Entries
                .Where(e => ParseUtc(e.StartUtc) is DateTime start && start < cutoff)
                .OrderBy(e => e.StartUtc, StringComparer.Ordinal)
                .ToList();

            var state = new ChannelScheduleState
            {
                ChannelId = channel.Id,
                Entries = preserved
            };

            if (string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
            {
                state.SmartTemplateCreatedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
                state.SmartSeriesPattern = BuildSmartPattern(channel);
                channel.SmartTemplateCreatedUtc = state.SmartTemplateCreatedUtc;
            }

            if (string.Equals(channel.SchedulingMethod, "RepeatingSchedule", StringComparison.OrdinalIgnoreCase)
                && string.Equals(channel.ChannelType, "Series", StringComparison.OrdinalIgnoreCase))
            {
                channel.RepeatingSeriesOrder = Shuffle(channel.Content.Select(c => c.ItemId).Where(IsValidGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), null);
            }

            GenerateRange(channel, state, cutoff, GetTargetEndUtc(channel, nowUtc), cancellationToken);
            channel.NeedsReconcile = false;
            _store.Save(state);
            PersistMutableChannelState();
            return state;
        }
    }

    public ChannelScheduleState Reconcile(ChannelConfiguration channel, DateTime nowUtc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var previous = _store.Load(channel.Id);
            if (previous.Entries.Count == 0)
            {
                return GenerateNew(channel, nowUtc, cancellationToken);
            }

            var cutoff = GetRegenerationCutoff(channel, previous, nowUtc);
            var state = new ChannelScheduleState
            {
                ChannelId = channel.Id,
                Entries = previous.Entries
                    .Where(e => ParseUtc(e.StartUtc) is DateTime start && start < cutoff)
                    .OrderBy(e => e.StartUtc, StringComparer.Ordinal)
                    .ToList(),
                SeriesCycle = previous.SeriesCycle,
                SeriesCycleIndex = previous.SeriesCycleIndex,
                ContentShuffleStates = previous.ContentShuffleStates,
                SeriesCursors = previous.SeriesCursors,
                SmartTemplateCreatedUtc = previous.SmartTemplateCreatedUtc,
                SmartSeriesPattern = previous.SmartSeriesPattern
            };

            if (string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase)
                && state.SmartSeriesPattern.Count == 0)
            {
                state.SmartSeriesPattern = BuildSmartPattern(channel);
                state.SmartTemplateCreatedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture);
            }

            GenerateRange(channel, state, cutoff, GetTargetEndUtc(channel, nowUtc), cancellationToken);
            channel.NeedsReconcile = false;
            _store.Save(state);
            PersistMutableChannelState();
            return state;
        }
    }

    public ChannelScheduleState Extend(ChannelConfiguration channel, DateTime nowUtc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var state = _store.Load(channel.Id);
            if (state.Entries.Count == 0)
            {
                return GenerateNew(channel, nowUtc, cancellationToken);
            }

            ExtendInternal(channel, state, nowUtc, cancellationToken);
            _store.Save(state);
            PersistMutableChannelState();
            return state;
        }
    }

    public PlaybackResolution ResolvePlayback(ChannelConfiguration channel, DateTime nowUtc)
    {
        lock (_gate)
        {
            var state = Ensure(channel, nowUtc);
            var entry = state.Entries
                .Select(e => new { Entry = e, Start = ParseUtc(e.StartUtc), End = ParseUtc(e.EndUtc) })
                .Where(x => x.Start.HasValue && x.End.HasValue && x.Start.Value <= nowUtc && x.End.Value > nowUtc)
                .OrderBy(x => x.Start)
                .Select(x => x.Entry)
                .FirstOrDefault();

            if (entry is null)
            {
                return PlaybackResolution.Message("Schedule Not Available", "Schedule needs to be generated.");
            }

            if (string.Equals(entry.Kind, "OffAir", StringComparison.OrdinalIgnoreCase))
            {
                return PlaybackResolution.Message("Off Air", entry.Overview);
            }

            if (string.Equals(entry.Kind, "ContentNotAvailable", StringComparison.OrdinalIgnoreCase))
            {
                return PlaybackResolution.Message("Content Not Available", "Channel has no content.");
            }

            if (string.Equals(entry.Kind, "DynamicSeries", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveDynamicSeries(channel, entry);
            }

            if (string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase)
                && string.Equals(channel.ContentMode, "RandomUnwatched", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMovieRandomUnwatched(channel, entry);
            }

            if (!Guid.TryParse(entry.ItemId, out var itemId))
            {
                return PlaybackResolution.Message("Content Not Available", "The scheduled item no longer exists.");
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                var fallback = ResolveMissingTraditional(channel, state, entry);
                return fallback ?? PlaybackResolution.Message("Content Not Available", "The scheduled item is unavailable.");
            }

            var start = ParseUtc(entry.StartUtc) ?? nowUtc;
            var offset = nowUtc > start ? nowUtc - start : TimeSpan.Zero;
            var runtime = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value) : TimeSpan.Zero;
            if (runtime > TimeSpan.Zero && offset >= runtime)
            {
                offset = runtime - TimeSpan.FromSeconds(1);
            }

            return new PlaybackResolution(item, entry, offset, false, string.Empty, string.Empty);
        }
    }

    public IReadOnlyList<ScheduleEntry> GetEntries(ChannelConfiguration channel, DateTime fromUtc, DateTime toUtc)
    {
        var state = Ensure(channel, DateTime.UtcNow);
        return state.Entries
            .Where(e =>
            {
                var start = ParseUtc(e.StartUtc);
                var end = ParseUtc(e.EndUtc);
                return start.HasValue && end.HasValue && end.Value > fromUtc && start.Value < toUtc;
            })
            .OrderBy(e => e.StartUtc, StringComparer.Ordinal)
            .ToList();
    }

    private void ExtendInternal(ChannelConfiguration channel, ChannelScheduleState state, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var historyCutoff = nowUtc - HistoryRetention;
        state.Entries = state.Entries
            .Where(e => ParseUtc(e.EndUtc) is not DateTime end || end >= historyCutoff)
            .OrderBy(e => e.StartUtc, StringComparer.Ordinal)
            .ToList();

        var cursor = state.Entries
            .Select(e => ParseUtc(e.EndUtc))
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .DefaultIfEmpty(GetInitialAnchorUtc(channel, nowUtc))
            .Max();

        if (string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
        {
            MaybeRotateSmartTemplate(channel, state, cursor, nowUtc);
        }

        var target = GetTargetEndUtc(channel, nowUtc);
        if (cursor < target)
        {
            GenerateRange(channel, state, cursor, target, cancellationToken);
        }
    }

    private void GenerateRange(
        ChannelConfiguration channel,
        ChannelScheduleState state,
        DateTime startUtc,
        DateTime targetEndUtc,
        CancellationToken cancellationToken)
    {
        var cursor = startUtc;
        var safety = 0;

        while (cursor < targetEndUtc && safety++ < 50000)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsOnAir(channel, cursor))
            {
                var nextOnAir = GetNextOnAirUtc(channel, cursor);
                var end = nextOnAir > cursor ? nextOnAir : cursor.AddHours(1);
                AddEntry(state, channel.Id, cursor, end, "OffAir", string.Empty, string.Empty, "Off Air", string.Empty,
                    "Back on air at " + TimeZoneInfo.ConvertTimeFromUtc(end, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture),
                    null, null, string.Empty);
                cursor = end;
                continue;
            }

            var eligibleContent = channel.Content
                .Where(c => Guid.TryParse(c.ItemId, out var id) && _libraryManager.GetItemById(id) is not null)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (eligibleContent.Count == 0)
            {
                AddEntry(state, channel.Id, cursor, targetEndUtc, "ContentNotAvailable", string.Empty, string.Empty,
                    "Content Not Available", string.Empty, "Channel has no content.", null, null, string.Empty);
                break;
            }

            var before = cursor;
            if (string.Equals(channel.ChannelType, "Movies", StringComparison.OrdinalIgnoreCase))
            {
                cursor = GenerateMovieEntry(channel, state, cursor, eligibleContent);
            }
            else
            {
                cursor = GenerateSeriesBlock(channel, state, cursor, eligibleContent);
            }

            if (cursor <= before)
            {
                _logger.LogWarning("Virtual TV scheduler made no progress for channel {ChannelId}; adding a 30 minute placeholder.", channel.Id);
                cursor = before.AddMinutes(30);
            }
        }
    }

    private DateTime GenerateMovieEntry(
        ChannelConfiguration channel,
        ChannelScheduleState state,
        DateTime cursor,
        IReadOnlyList<ChannelContentConfiguration> content)
    {
        var items = content
            .Select(c => Guid.TryParse(c.ItemId, out var id) ? _libraryManager.GetItemById(id) : null)
            .OfType<Movie>()
            .Cast<BaseItem>()
            .ToList();

        if (items.Count == 0)
        {
            AddEntry(state, channel.Id, cursor, cursor.AddHours(1), "ContentNotAvailable", string.Empty, string.Empty,
                "Content Not Available", string.Empty, "Channel has no movies.", null, null, string.Empty);
            return cursor.AddHours(1);
        }

        if (string.Equals(channel.ContentMode, "RandomUnwatched", StringComparison.OrdinalIgnoreCase))
        {
            var user = GetOwnerUser(channel);
            var pool = user is null
                ? items
                : items.Where(i => !IsWatched(user, i)).ToList();

            var sourcePool = pool.Count > 0 ? pool : items;
            var selectedId = NextShuffleId(state, pool.Count > 0 ? "movies-unwatched" : "movies-fallback", sourcePool.Select(i => i.Id.ToString("N")).ToList());
            var item = sourcePool.First(i => string.Equals(i.Id.ToString("N"), selectedId, StringComparison.OrdinalIgnoreCase));
            var end = cursor.AddMinutes(NormalizeDynamicMinutes(channel.DynamicBlockMinutes, true));

            AddEntry(state, channel.Id, cursor, end, "Content", item.Id.ToString("N"), string.Empty,
                item.Name, string.Empty, item.Overview ?? string.Empty, null, null, "RandomUnwatched");
            return end;
        }

        var movieId = NextShuffleId(state, "movies", items.Select(i => i.Id.ToString("N")).ToList());
        var movie = items.First(i => string.Equals(i.Id.ToString("N"), movieId, StringComparison.OrdinalIgnoreCase));
        var duration = GetDuration(movie, TimeSpan.FromMinutes(90));
        var movieEnd = cursor.Add(duration);

        AddEntry(state, channel.Id, cursor, movieEnd, "Content", movie.Id.ToString("N"), string.Empty,
            movie.Name, string.Empty, movie.Overview ?? string.Empty, null, null, "Random");
        return movieEnd;
    }

    private DateTime GenerateSeriesBlock(
        ChannelConfiguration channel,
        ChannelScheduleState state,
        DateTime cursor,
        IReadOnlyList<ChannelContentConfiguration> content)
    {
        var selected = SelectSeries(channel, state, content);
        if (selected is null)
        {
            AddEntry(state, channel.Id, cursor, cursor.AddHours(1), "ContentNotAvailable", string.Empty, string.Empty,
                "Content Not Available", string.Empty, "Channel has no eligible series.", null, null, string.Empty);
            return cursor.AddHours(1);
        }

        if (IsWatchedDependent(channel))
        {
            var end = cursor.AddMinutes(NormalizeDynamicMinutes(channel.DynamicBlockMinutes, false));
            AddEntry(state, channel.Id, cursor, end, "DynamicSeries", string.Empty, selected.ItemId,
                selected.Name, selected.Name, "Dynamic block — episode is resolved when playback starts.",
                null, null, channel.ContentMode);
            return end;
        }

        var episodes = GetEligibleEpisodes(channel, selected, orderedForChronology: string.Equals(channel.ContentMode, "Sequential", StringComparison.OrdinalIgnoreCase));
        if (episodes.Count == 0)
        {
            AddEntry(state, channel.Id, cursor, cursor.AddMinutes(30), "ContentNotAvailable", string.Empty, selected.ItemId,
                "Content Not Available", selected.Name, "No eligible episodes are available for this series.",
                null, null, channel.ContentMode);
            return cursor.AddMinutes(30);
        }

        var count = Math.Clamp(selected.ConsecutiveEpisodes, 1, 12);
        for (var i = 0; i < count; i++)
        {
            var episode = SelectTraditionalEpisode(channel, state, selected, episodes);
            if (episode is null)
            {
                break;
            }

            var duration = GetDuration(episode, TimeSpan.FromMinutes(22));
            var end = cursor.Add(duration);
            AddEntry(state, channel.Id, cursor, end, "Content", episode.Id.ToString("N"), selected.ItemId,
                episode.Name, selected.Name, episode.Overview ?? string.Empty,
                episode.ParentIndexNumber, episode.IndexNumber, channel.ContentMode);
            cursor = end;
        }

        return cursor;
    }

    private ChannelContentConfiguration? SelectSeries(
        ChannelConfiguration channel,
        ChannelScheduleState state,
        IReadOnlyList<ChannelContentConfiguration> content)
    {
        var eligible = content.Where(c => string.Equals(c.ItemType, "Series", StringComparison.OrdinalIgnoreCase)).ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        var id = NextSeriesId(channel, state, eligible.Select(c => c.ItemId).ToList());
        return eligible.FirstOrDefault(c => string.Equals(c.ItemId, id, StringComparison.OrdinalIgnoreCase)) ?? eligible[0];
    }

    private string NextSeriesId(ChannelConfiguration channel, ChannelScheduleState state, List<string> eligible)
    {
        eligible = eligible.Where(IsValidGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (eligible.Count == 0)
        {
            return string.Empty;
        }

        if (string.Equals(channel.SchedulingMethod, "ManualOrder", StringComparison.OrdinalIgnoreCase))
        {
            var order = channel.ManualSeriesOrder.Where(id => eligible.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            order.AddRange(eligible.Where(id => !order.Contains(id, StringComparer.OrdinalIgnoreCase)));
            if (state.SeriesCycle.Count == 0 || !state.SeriesCycle.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
            {
                state.SeriesCycle = order;
                state.SeriesCycleIndex = 0;
            }

            var result = state.SeriesCycle[state.SeriesCycleIndex % state.SeriesCycle.Count];
            state.SeriesCycleIndex = (state.SeriesCycleIndex + 1) % state.SeriesCycle.Count;
            return result;
        }

        if (string.Equals(channel.SchedulingMethod, "RepeatingSchedule", StringComparison.OrdinalIgnoreCase))
        {
            var order = channel.RepeatingSeriesOrder.Where(id => eligible.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (order.Count != eligible.Count)
            {
                order = Shuffle(eligible, null);
                channel.RepeatingSeriesOrder = order;
            }

            if (state.SeriesCycle.Count == 0 || !state.SeriesCycle.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
            {
                state.SeriesCycle = order;
                state.SeriesCycleIndex = 0;
            }

            var result = state.SeriesCycle[state.SeriesCycleIndex % state.SeriesCycle.Count];
            state.SeriesCycleIndex = (state.SeriesCycleIndex + 1) % state.SeriesCycle.Count;
            return result;
        }

        if (string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
        {
            if (state.SmartSeriesPattern.Count == 0
                || state.SmartSeriesPattern.Any(id => !eligible.Contains(id, StringComparer.OrdinalIgnoreCase))
                || eligible.Any(id => !state.SmartSeriesPattern.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                state.SmartSeriesPattern = BuildSmartPattern(channel);
                state.SmartTemplateCreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                channel.SmartTemplateCreatedUtc = state.SmartTemplateCreatedUtc;
                state.SeriesCycleIndex = 0;
            }

            var pattern = state.SmartSeriesPattern.Count > 0 ? state.SmartSeriesPattern : eligible;
            var result = pattern[state.SeriesCycleIndex % pattern.Count];
            state.SeriesCycleIndex = (state.SeriesCycleIndex + 1) % pattern.Count;
            return result;
        }

        // RandomSchedule: shuffle cycle over series.
        if (state.SeriesCycle.Count == 0
            || state.SeriesCycleIndex >= state.SeriesCycle.Count
            || state.SeriesCycle.Any(id => !eligible.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            var last = state.SeriesCycle.Count > 0 ? state.SeriesCycle.LastOrDefault() : null;
            state.SeriesCycle = Shuffle(eligible, last);
            state.SeriesCycleIndex = 0;
        }

        return state.SeriesCycle[state.SeriesCycleIndex++];
    }

    private Episode? SelectTraditionalEpisode(
        ChannelConfiguration channel,
        ChannelScheduleState state,
        ChannelContentConfiguration selected,
        IReadOnlyList<Episode> episodes)
    {
        if (string.Equals(channel.ContentMode, "Random", StringComparison.OrdinalIgnoreCase))
        {
            var id = NextShuffleId(state, "series:" + selected.ItemId, episodes.Select(e => e.Id.ToString("N")).ToList());
            return episodes.FirstOrDefault(e => string.Equals(e.Id.ToString("N"), id, StringComparison.OrdinalIgnoreCase));
        }

        var cursor = state.SeriesCursors.FirstOrDefault(c => string.Equals(c.SeriesId, selected.ItemId, StringComparison.OrdinalIgnoreCase));
        if (cursor is null)
        {
            cursor = new SeriesCursorState { SeriesId = selected.ItemId, Index = 0 };
            state.SeriesCursors.Add(cursor);
        }

        if (cursor.Index >= episodes.Count)
        {
            cursor.Index = 0;
        }

        return episodes[cursor.Index++ % episodes.Count];
    }

    private IReadOnlyList<Episode> GetEligibleEpisodes(
        ChannelConfiguration channel,
        ChannelContentConfiguration selected,
        bool orderedForChronology)
    {
        if (!Guid.TryParse(selected.ItemId, out var id)
            || _libraryManager.GetItemById(id) is not Series series)
        {
            return Array.Empty<Episode>();
        }

        var user = GetOwnerUser(channel) ?? _userManager.GetFirstUser();
        if (user is null)
        {
            return Array.Empty<Episode>();
        }

        var episodes = series.GetEpisodes(user, new DtoOptions(false) { EnableImages = false }, false)
            .OfType<Episode>()
            .Where(e => e.LocationType != MediaBrowser.Model.Entities.LocationType.Virtual)
            .ToList();

        if (!selected.AllSeasons && selected.SeasonIds.Count > 0)
        {
            episodes = episodes
                .Where(e => selected.SeasonIds.Contains(e.ParentId.ToString("N"), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (!selected.IncludeSpecials)
        {
            episodes = episodes.Where(e => (e.ParentIndexNumber ?? 0) > 0).ToList();
        }
        else if (orderedForChronology)
        {
            // Specials without a usable date are excluded from chronological modes.
            episodes = episodes
                .Where(e => (e.ParentIndexNumber ?? 0) > 0 || e.PremiereDate.HasValue)
                .ToList();
        }

        if (orderedForChronology && selected.IncludeSpecials)
        {
            return episodes
                .OrderBy(e => e.PremiereDate ?? DateTime.MaxValue)
                .ThenBy(e => e.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(e => e.IndexNumber ?? int.MaxValue)
                .ThenBy(e => e.SortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return episodes
            .OrderBy(e => e.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(e => e.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private PlaybackResolution ResolveDynamicSeries(ChannelConfiguration channel, ScheduleEntry entry)
    {
        var selected = channel.Content.FirstOrDefault(c => string.Equals(c.ItemId, entry.SeriesId, StringComparison.OrdinalIgnoreCase));
        var user = GetOwnerUser(channel);
        if (selected is null || user is null)
        {
            return PlaybackResolution.Message("Content Not Available", "The personal channel owner or series is unavailable.");
        }

        var chronological = string.Equals(channel.ContentMode, "NextUnwatched", StringComparison.OrdinalIgnoreCase);
        var episodes = GetEligibleEpisodes(channel, selected, chronological).ToList();
        if (episodes.Count == 0)
        {
            return PlaybackResolution.Message("Content Not Available", "No eligible episodes are available.");
        }

        Episode item;
        if (chronological)
        {
            item = episodes.FirstOrDefault(e => !IsWatched(user, e))
                ?? ChooseRuntimeRandom(channel.Id + ":" + selected.ItemId + ":nu-fallback", episodes);
        }
        else
        {
            var pool = episodes.Where(e => !IsWatched(user, e)).ToList();
            item = pool.Count > 0
                ? ChooseRuntimeRandom(channel.Id + ":" + selected.ItemId + ":ru", pool)
                : ChooseRuntimeRandom(channel.Id + ":" + selected.ItemId + ":ru-fallback", episodes);
        }

        var resume = GetResume(user, item);
        return new PlaybackResolution(item, entry, TimeSpan.FromTicks(Math.Max(0, resume)), true, string.Empty, string.Empty);
    }

    private PlaybackResolution ResolveMovieRandomUnwatched(ChannelConfiguration channel, ScheduleEntry entry)
    {
        var user = GetOwnerUser(channel);
        if (user is null)
        {
            return PlaybackResolution.Message("Content Not Available", "The personal channel owner is unavailable.");
        }

        BaseItem? scheduled = null;
        if (Guid.TryParse(entry.ItemId, out var scheduledId))
        {
            scheduled = _libraryManager.GetItemById(scheduledId);
        }

        if (scheduled is not null && !IsWatched(user, scheduled))
        {
            return new PlaybackResolution(scheduled, entry, TimeSpan.FromTicks(Math.Max(0, GetResume(user, scheduled))), true, string.Empty, string.Empty);
        }

        var all = channel.Content
            .Select(c => Guid.TryParse(c.ItemId, out var id) ? _libraryManager.GetItemById(id) : null)
            .OfType<Movie>()
            .Cast<BaseItem>()
            .ToList();

        var unwatched = all.Where(i => !IsWatched(user, i)).ToList();
        var pool = unwatched.Count > 0 ? unwatched : all;
        if (pool.Count == 0)
        {
            return PlaybackResolution.Message("Content Not Available", "Channel has no movies.");
        }

        var replacement = ChooseRuntimeRandom(channel.Id + ":movie-ru", pool);
        var message = scheduled is not null && IsWatched(user, scheduled)
            ? "This movie has already been watched. Selecting another unwatched movie…"
            : string.Empty;

        return new PlaybackResolution(replacement, entry, TimeSpan.FromTicks(Math.Max(0, GetResume(user, replacement))), true, message, string.Empty);
    }

    private PlaybackResolution? ResolveMissingTraditional(ChannelConfiguration channel, ChannelScheduleState state, ScheduleEntry entry)
    {
        if (string.Equals(channel.ContentMode, "Sequential", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entry.SeriesId))
        {
            var selected = channel.Content.FirstOrDefault(c => string.Equals(c.ItemId, entry.SeriesId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return null;
            }

            var episodes = GetEligibleEpisodes(channel, selected, true);
            var next = episodes.FirstOrDefault(e =>
                ParseEpisodePosition(e) > (entry.SeasonNumber ?? -1, entry.EpisodeNumber ?? -1));
            next ??= episodes.FirstOrDefault();
            if (next is not null)
            {
                return new PlaybackResolution(next, entry, TimeSpan.Zero, false,
                    "This content is not available. Playing the next sequential episode…", string.Empty);
            }
        }

        if (string.Equals(channel.ContentMode, "Random", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = channel.ChannelType == "Movies"
                ? channel.Content.Select(c => Guid.TryParse(c.ItemId, out var id) ? _libraryManager.GetItemById(id) : null).Where(i => i is Movie).Cast<BaseItem>().ToList()
                : channel.Content.SelectMany(c => GetEligibleEpisodes(channel, c, false)).Cast<BaseItem>().ToList();

            if (candidates.Count > 0)
            {
                var replacement = ChooseRuntimeRandom(channel.Id + ":missing", candidates);
                return new PlaybackResolution(replacement, entry, TimeSpan.Zero, false,
                    "This content is not available. Selecting another random episode/movie…", string.Empty);
            }
        }

        return null;
    }

    private static (int Season, int Episode) ParseEpisodePosition(Episode episode)
        => (episode.ParentIndexNumber ?? int.MaxValue, episode.IndexNumber ?? int.MaxValue);

    private BaseItem ChooseRuntimeRandom(string key, IReadOnlyList<BaseItem> items)
    {
        if (items.Count == 1)
        {
            _runtimeLastRandom[key] = items[0].Id.ToString("N");
            return items[0];
        }

        _runtimeLastRandom.TryGetValue(key, out var last);
        var candidates = items.Where(i => !string.Equals(i.Id.ToString("N"), last, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = items.ToList();
        }

        var item = candidates[Random.Shared.Next(candidates.Count)];
        _runtimeLastRandom[key] = item.Id.ToString("N");
        return item;
    }

    private string NextShuffleId(ChannelScheduleState state, string key, List<string> ids)
    {
        ids = ids.Where(IsValidGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            return string.Empty;
        }

        var shuffle = state.ContentShuffleStates.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        if (shuffle is null)
        {
            shuffle = new ShuffleState { Key = key };
            state.ContentShuffleStates.Add(shuffle);
        }

        var setChanged = shuffle.ItemIds.Any(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            || ids.Any(id => !shuffle.ItemIds.Contains(id, StringComparer.OrdinalIgnoreCase));

        if (shuffle.ItemIds.Count == 0 || shuffle.Index >= shuffle.ItemIds.Count || setChanged)
        {
            shuffle.ItemIds = Shuffle(ids, shuffle.LastItemId);
            shuffle.Index = 0;
        }

        var result = shuffle.ItemIds[shuffle.Index++];
        shuffle.LastItemId = result;
        return result;
    }

    private static List<string> Shuffle(List<string> ids, string? avoidFirst)
    {
        var result = ids.ToList();
        for (var i = result.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        if (result.Count > 1 && !string.IsNullOrEmpty(avoidFirst)
            && string.Equals(result[0], avoidFirst, StringComparison.OrdinalIgnoreCase))
        {
            (result[0], result[1]) = (result[1], result[0]);
        }

        return result;
    }

    private static List<string> BuildSmartPattern(ChannelConfiguration channel)
    {
        var ids = channel.Content
            .Where(c => string.Equals(c.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.SortOrder)
            .Select(c => c.ItemId)
            .Where(IsValidGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count <= 1)
        {
            return ids;
        }

        var first = Shuffle(ids, null);
        var second = Shuffle(ids, first.LastOrDefault());
        return first.Concat(second).ToList();
    }

    private void MaybeRotateSmartTemplate(ChannelConfiguration channel, ChannelScheduleState state, DateTime cursorUtc, DateTime nowUtc)
    {
        if (!string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var created = ParseUtc(state.SmartTemplateCreatedUtc)
            ?? ParseUtc(channel.SmartTemplateCreatedUtc)
            ?? nowUtc;
        var months = channel.SmartRotationMonths is 1 or 2 or 3 or 6 ? channel.SmartRotationMonths : 2;
        var threshold = created.AddMonths(months);
        var cursorLocal = TimeZoneInfo.ConvertTimeFromUtc(cursorUtc, TimeZoneInfo.Local);
        var sundayLocal = StartOfWeekSunday(cursorLocal);

        if (sundayLocal.ToUniversalTime() >= threshold)
        {
            state.SmartSeriesPattern = BuildSmartPattern(channel);
            state.SeriesCycleIndex = 0;
            state.SmartTemplateCreatedUtc = sundayLocal.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            channel.SmartTemplateCreatedUtc = state.SmartTemplateCreatedUtc;
        }
    }

    private User? GetOwnerUser(ChannelConfiguration channel)
    {
        if (Guid.TryParse(channel.OwnerUserId, out var ownerId))
        {
            var owner = _userManager.GetUserById(ownerId);
            if (owner is not null)
            {
                return owner;
            }
        }

        return _userManager.GetUsers().FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator))
            ?? _userManager.GetFirstUser();
    }

    private bool IsWatched(User user, BaseItem item)
        => _userDataManager.GetUserData(user, item)?.Played ?? false;

    private long GetResume(User user, BaseItem item)
        => _userDataManager.GetUserData(user, item)?.PlaybackPositionTicks ?? 0;

    private static bool IsWatchedDependent(ChannelConfiguration channel)
        => string.Equals(channel.ContentMode, "NextUnwatched", StringComparison.OrdinalIgnoreCase)
            || string.Equals(channel.ContentMode, "RandomUnwatched", StringComparison.OrdinalIgnoreCase);

    private static int NormalizeDynamicMinutes(int value, bool movie)
    {
        var allowed = movie ? new[] { 15, 20, 30, 40, 45, 60, 90 } : new[] { 15, 20, 30, 40, 45, 60 };
        return allowed.Contains(value) ? value : (movie ? 90 : 30);
    }

    private static TimeSpan GetDuration(BaseItem item, TimeSpan fallback)
        => item.RunTimeTicks is long ticks && ticks > 0 ? TimeSpan.FromTicks(ticks) : fallback;

    private static bool IsValidGuid(string value) => Guid.TryParse(value, out _);

    private static DateTime? ParseUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static DateTime GetInitialAnchorUtc(ChannelConfiguration channel, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        DateTime anchorLocal;
        if (string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
        {
            anchorLocal = StartOfWeekSunday(local);
        }
        else
        {
            anchorLocal = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        }

        return TimeZoneInfo.ConvertTimeToUtc(anchorLocal, TimeZoneInfo.Local);
    }

    private static DateTime GetTargetEndUtc(ChannelConfiguration channel, DateTime nowUtc)
    {
        var target = nowUtc + FutureHorizon;
        if (!string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(target, TimeZoneInfo.Local);
        var daysUntilSaturday = ((int)DayOfWeek.Saturday - (int)local.DayOfWeek + 7) % 7;
        var endLocal = local.Date.AddDays(daysUntilSaturday + 1);
        return TimeZoneInfo.ConvertTimeToUtc(endLocal, TimeZoneInfo.Local);
    }

    private static DateTime GetRegenerationCutoff(ChannelConfiguration channel, ChannelScheduleState state, DateTime nowUtc)
    {
        if (string.Equals(channel.SchedulingMethod, "SmartSchedule", StringComparison.OrdinalIgnoreCase))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
            var sunday = StartOfWeekSunday(local).AddDays(7);
            return TimeZoneInfo.ConvertTimeToUtc(sunday, TimeZoneInfo.Local);
        }

        var current = state.Entries
            .Select(e => new { Entry = e, Start = ParseUtc(e.StartUtc), End = ParseUtc(e.EndUtc) })
            .FirstOrDefault(x => x.Start.HasValue && x.End.HasValue && x.Start.Value <= nowUtc && x.End.Value > nowUtc);
        if (current?.End is DateTime currentEnd)
        {
            return currentEnd;
        }

        var firstFuture = state.Entries
            .Select(e => ParseUtc(e.StartUtc))
            .Where(d => d.HasValue && d.Value >= nowUtc)
            .Select(d => d!.Value)
            .OrderBy(d => d)
            .FirstOrDefault();

        return firstFuture == default ? nowUtc : firstFuture;
    }

    private static DateTime StartOfWeekSunday(DateTime local)
    {
        var date = local.Date;
        return DateTime.SpecifyKind(date.AddDays(-(int)date.DayOfWeek), DateTimeKind.Unspecified);
    }

    private static bool IsOnAir(ChannelConfiguration channel, DateTime utc)
    {
        if (channel.Is24Hours)
        {
            return true;
        }

        if (!TimeSpan.TryParse(channel.OnAirStart, CultureInfo.InvariantCulture, out var onAir)
            || !TimeSpan.TryParse(channel.OffAirStart, CultureInfo.InvariantCulture, out var offAir))
        {
            onAir = TimeSpan.FromHours(7);
            offAir = TimeSpan.FromHours(2);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        var time = local.TimeOfDay;

        if (offAir <= onAir)
        {
            return time >= onAir || time < offAir;
        }

        return time >= onAir && time < offAir;
    }

    private static DateTime GetNextOnAirUtc(ChannelConfiguration channel, DateTime utc)
    {
        if (channel.Is24Hours)
        {
            return utc;
        }

        if (!TimeSpan.TryParse(channel.OnAirStart, CultureInfo.InvariantCulture, out var onAir))
        {
            onAir = TimeSpan.FromHours(7);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        var candidate = DateTime.SpecifyKind(local.Date + onAir, DateTimeKind.Unspecified);
        if (candidate <= local)
        {
            candidate = candidate.AddDays(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(candidate, TimeZoneInfo.Local);
    }

    private static void AddEntry(
        ChannelScheduleState state,
        string channelId,
        DateTime start,
        DateTime end,
        string kind,
        string itemId,
        string seriesId,
        string itemName,
        string seriesName,
        string overview,
        int? season,
        int? episode,
        string dynamicRule)
    {
        state.Entries.Add(new ScheduleEntry
        {
            ChannelId = channelId,
            StartUtc = start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            EndUtc = end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Kind = kind,
            ItemId = itemId,
            SeriesId = seriesId,
            ItemName = itemName,
            SeriesName = seriesName,
            Overview = overview,
            SeasonNumber = season,
            EpisodeNumber = episode,
            DynamicRule = dynamicRule
        });
    }

    private static void PersistMutableChannelState()
    {
        Plugin.Instance?.SaveConfiguration();
    }
}

/// <summary>
/// Result of resolving a channel at the current wall-clock time.
/// </summary>
public sealed record PlaybackResolution(
    BaseItem? Item,
    ScheduleEntry? Entry,
    TimeSpan SourceOffset,
    bool WatchedDependent,
    string UserMessage,
    string Status)
{
    public static PlaybackResolution Message(string status, string message)
        => new(null, null, TimeSpan.Zero, false, message, status);
}
