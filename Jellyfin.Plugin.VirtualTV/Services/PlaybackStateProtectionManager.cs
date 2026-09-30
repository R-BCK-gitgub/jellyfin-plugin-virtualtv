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
/// Preserves Jellyfin watched/resume state for playback modes that must behave like television
/// rather than personal progress tracking. The first snapshot captured for an item in a managed
/// session is retained for the entire session and is never overwritten by later playback state.
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

    /// <summary>
    /// Ensures every supplied root item is protected. Existing snapshots are preserved rather
    /// than recaptured, which prevents a natural episode transition from replacing the original
    /// Unwatched/Resume state with a state Jellyfin has already modified during the same session.
    /// </summary>
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
                "Virtual TV protects {RootCount} original Jellyfin item state(s) for session {SessionId}.",
                snapshot.Roots.Count,
                sessionId);

            return snapshot.Roots.Count > 0;
        }
    }

    /// <summary>
    /// Restores the original state for the item that emitted a playback event.
    /// </summary>
    public void RestoreIfProtected(PlaybackProgressEventArgs eventArgs, bool clearAfterRestore)
    {
        if (eventArgs.Session is null
            || eventArgs.Item is null
            || !_active.TryGetValue(eventArgs.Session.Id, out var snapshot))
        {
            return;
        }

        if (DateTime.UtcNow - snapshot.CreatedUtc > MaximumProtectionAge)
        {
            CancelProtection(eventArgs.Session.Id, restore: true);
            return;
        }

        lock (snapshot.Gate)
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.PlaySessionId))
            {
                if (snapshot.IsClosing && !snapshot.PlaySessionIds.Contains(eventArgs.PlaySessionId))
                {
                    return;
                }

                snapshot.PlaySessionIds.Add(eventArgs.PlaySessionId);
            }

            var root = snapshot.Roots.FirstOrDefault(candidate =>
                candidate.RootItemId == eventArgs.Item.Id
                || candidate.States.Any(state => state.ItemId == eventArgs.Item.Id));

            if (root is null)
            {
                return;
            }

            RestoreRoot(snapshot.UserId, root);

            // The snapshot intentionally stays active through natural queue transitions.
            // "clearAfterRestore" remains in the signature for compatibility with older
            // consumers but final cleanup belongs to CancelProtection/EndSession.
            _ = clearAfterRestore;
        }
    }

    /// <summary>
    /// Re-applies every protected state without ending the protection session.
    /// Used after a stop event because Jellyfin updates watched state before plugins receive it.
    /// </summary>
    public void RestoreAllIfProtected(string sessionId)
    {
        if (!_active.TryGetValue(sessionId, out var snapshot))
        {
            return;
        }

        lock (snapshot.Gate)
        {
            RestoreSnapshot(snapshot);
        }
    }

    /// <summary>
    /// Restores the original state immediately, then keeps the snapshot briefly so late
    /// progress/stop reports from the same Jellyfin play session cannot leak into
    /// Continue Watching after the Virtual TV session ends.
    /// </summary>
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

    /// <summary>
    /// Restores all original states and removes the session snapshot.
    /// </summary>
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
