using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Protects the original Jellyfin user state for television-style Personalized TV playback.
///
/// Invariant:
/// 1. snapshot once before PlayNow;
/// 2. never write user data while the VOD player is running;
/// 3. restore the stopped item after Jellyfin has processed PlaybackStop;
/// 4. perform one delayed final restore when the Virtual TV session closes, so a late Jellyfin
///    event cannot leak into Continue Watching.
/// </summary>
public sealed class PlaybackStateProtectionManager
{
    private static readonly TimeSpan MaximumProtectionAge = TimeSpan.FromHours(4);
    private static readonly TimeSpan FinalRestoreGrace = TimeSpan.FromSeconds(5);

    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackStateProtectionManager> _logger;
    private readonly ConcurrentDictionary<string, PlaybackStateSnapshot> _active = new(StringComparer.Ordinal);

    public PlaybackStateProtectionManager(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<PlaybackStateProtectionManager> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public bool BeginProtection(string sessionId, IEnumerable<Guid> rootItemIds, Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        if (_active.TryGetValue(sessionId, out var existing)
            && (existing.UserId != userId
                || existing.IsClosing
                || DateTime.UtcNow - existing.CreatedUtc > MaximumProtectionAge))
        {
            CancelProtection(sessionId, restore: true);
        }

        var snapshot = _active.GetOrAdd(
            sessionId,
            _ => new PlaybackStateSnapshot(userId, new List<RootItemSnapshot>(), DateTime.UtcNow));

        lock (snapshot.Gate)
        {
            foreach (var rootItemId in rootItemIds.Distinct())
            {
                if (snapshot.Roots.Any(root => root.RootItemId == rootItemId))
                {
                    continue;
                }

                var rootItem = _libraryManager.GetItemById(rootItemId);
                if (rootItem is null)
                {
                    continue;
                }

                IReadOnlyList<BaseItem> protectedItems = rootItem is Video video
                    ? video.GetAllVersions().Cast<BaseItem>().ToArray()
                    : [rootItem];

                var states = new List<ItemUserStateSnapshot>(protectedItems.Count);
                foreach (var protectedItem in protectedItems)
                {
                    var data = _userDataManager.GetUserData(user, protectedItem);
                    if (data is null)
                    {
                        continue;
                    }

                    states.Add(new ItemUserStateSnapshot(
                        protectedItem.Id,
                        data.PlaybackPositionTicks,
                        data.Played,
                        data.PlayCount,
                        data.LastPlayedDate,
                        data.AudioStreamIndex,
                        data.SubtitleStreamIndex));
                }

                if (states.Count > 0)
                {
                    snapshot.Roots.Add(new RootItemSnapshot(rootItemId, states));
                }
            }

            _logger.LogDebug(
                "Virtual TV captured {RootCount} original Jellyfin item state(s) for session {SessionId}. No user-data restore runs while playback is active.",
                snapshot.Roots.Count,
                sessionId);

            return snapshot.Roots.Count > 0;
        }
    }

    public void RestoreStoppedItem(string sessionId, Guid itemId, string? playSessionId)
    {
        if (!_active.TryGetValue(sessionId, out var snapshot))
        {
            return;
        }

        if (DateTime.UtcNow - snapshot.CreatedUtc > MaximumProtectionAge)
        {
            CancelProtection(sessionId, restore: true);
            return;
        }

        lock (snapshot.Gate)
        {
            if (!string.IsNullOrWhiteSpace(playSessionId))
            {
                snapshot.PlaySessionIds.Add(playSessionId);
            }

            var root = snapshot.Roots.FirstOrDefault(candidate =>
                candidate.RootItemId == itemId
                || candidate.States.Any(state => state.ItemId == itemId));

            if (root is null)
            {
                return;
            }

            RestoreRoot(snapshot.UserId, root);

            _logger.LogDebug(
                "Virtual TV restored stopped item {ItemId} to its original Jellyfin state for session {SessionId}.",
                itemId,
                sessionId);
        }
    }

    public void CompleteProtection(string sessionId)
    {
        if (!_active.TryGetValue(sessionId, out var snapshot))
        {
            return;
        }

        lock (snapshot.Gate)
        {
            snapshot.IsClosing = true;
            RestoreSnapshot(snapshot);
        }

        _ = FinalizeProtectionAsync(sessionId, snapshot);
    }

    private async Task FinalizeProtectionAsync(string sessionId, PlaybackStateSnapshot snapshot)
    {
        await Task.Delay(FinalRestoreGrace).ConfigureAwait(false);

        if (!_active.TryGetValue(sessionId, out var current)
            || !ReferenceEquals(current, snapshot))
        {
            return;
        }

        lock (snapshot.Gate)
        {
            RestoreSnapshot(snapshot);
        }

        if (_active.TryGetValue(sessionId, out current)
            && ReferenceEquals(current, snapshot))
        {
            _active.TryRemove(sessionId, out _);
        }
    }

    public void CancelProtection(string sessionId, bool restore)
    {
        if (!_active.TryRemove(sessionId, out var snapshot))
        {
            return;
        }

        if (!restore)
        {
            return;
        }

        lock (snapshot.Gate)
        {
            RestoreSnapshot(snapshot);
        }
    }

    private void RestoreSnapshot(PlaybackStateSnapshot snapshot)
    {
        foreach (var root in snapshot.Roots)
        {
            RestoreRoot(snapshot.UserId, root);
        }
    }

    private void RestoreRoot(Guid userId, RootItemSnapshot root)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        foreach (var state in root.States)
        {
            var item = _libraryManager.GetItemById(state.ItemId);
            if (item is null)
            {
                continue;
            }

            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                continue;
            }

            data.PlaybackPositionTicks = state.PlaybackPositionTicks;
            data.Played = state.Played;
            data.PlayCount = state.PlayCount;
            data.LastPlayedDate = state.LastPlayedDate;
            data.AudioStreamIndex = state.AudioStreamIndex;
            data.SubtitleStreamIndex = state.SubtitleStreamIndex;

            _userDataManager.SaveUserData(
                user,
                item,
                data,
                UserDataSaveReason.UpdateUserData,
                CancellationToken.None);
        }
    }

    private sealed class PlaybackStateSnapshot
    {
        public PlaybackStateSnapshot(Guid userId, List<RootItemSnapshot> roots, DateTime createdUtc)
        {
            UserId = userId;
            Roots = roots;
            CreatedUtc = createdUtc;
        }

        public object Gate { get; } = new();
        public Guid UserId { get; }
        public List<RootItemSnapshot> Roots { get; }
        public HashSet<string> PlaySessionIds { get; } = new(StringComparer.Ordinal);
        public bool IsClosing { get; set; }
        public DateTime CreatedUtc { get; }
    }

    private sealed record RootItemSnapshot(
        Guid RootItemId,
        IReadOnlyList<ItemUserStateSnapshot> States);

    private sealed record ItemUserStateSnapshot(
        Guid ItemId,
        long PlaybackPositionTicks,
        bool Played,
        int PlayCount,
        DateTime? LastPlayedDate,
        int? AudioStreamIndex,
        int? SubtitleStreamIndex);
}
