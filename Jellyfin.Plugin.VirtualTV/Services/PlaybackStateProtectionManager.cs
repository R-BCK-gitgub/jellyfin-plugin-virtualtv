using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Preserves Jellyfin watched/resume state for every library item temporarily placed in a
/// Virtual TV managed queue. Snapshots are captured before the queue is sent to the client,
/// so even an automatic transition cannot increment Play Count or create Continue Watching
/// state that survives the Virtual TV session.
/// </summary>
public sealed class PlaybackStateProtectionManager
{
    private static readonly TimeSpan MaximumProtectionAge = TimeSpan.FromHours(2);

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
    /// Replaces the protection set for a Virtual TV session. Any previous snapshot is restored
    /// before the new managed queue is captured, so repeated re-sync commands remain lossless.
    /// </summary>
    public bool BeginProtection(string sessionId, IEnumerable<Guid> rootItemIds, Guid userId)
    {
        CancelProtection(sessionId, restore: true);

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        var roots = new List<RootItemSnapshot>();

        foreach (var rootItemId in rootItemIds.Distinct())
        {
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
                    data.LastPlayedDate));
            }

            if (states.Count > 0)
            {
                roots.Add(new RootItemSnapshot(rootItemId, states));
            }
        }

        if (roots.Count == 0)
        {
            return false;
        }

        _active[sessionId] = new PlaybackStateSnapshot(
            userId,
            roots,
            DateTime.UtcNow);

        _logger.LogDebug(
            "Virtual TV protected Jellyfin user state for session {SessionId}: {RootCount} managed queue item(s).",
            sessionId,
            roots.Count);

        return true;
    }

    /// <summary>
    /// Restores the snapshot associated with the item that just reported playback activity.
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

        var root = snapshot.Roots.FirstOrDefault(candidate =>
            candidate.RootItemId == eventArgs.Item.Id
            || candidate.States.Any(state => state.ItemId == eventArgs.Item.Id));

        if (root is null)
        {
            return;
        }

        RestoreRoot(snapshot.UserId, root);

        if (clearAfterRestore)
        {
            // Do not remove the whole session snapshot here. A natural Virtual TV transition
            // may immediately start another protected queue item. Session cleanup is owned by
            // LiveTvPlaybackCoordinator when the user actually leaves the channel.
            _logger.LogDebug(
                "Virtual TV restored completed item {ItemId} while retaining queue protection for session {SessionId}.",
                root.RootItemId,
                eventArgs.Session.Id);
        }
    }

    /// <summary>
    /// Restores all managed queue items and removes the protection context.
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

            _userDataManager.SaveUserData(
                user,
                item,
                data,
                UserDataSaveReason.UpdateUserData,
                CancellationToken.None);
        }
    }

    private sealed record PlaybackStateSnapshot(
        Guid UserId,
        IReadOnlyList<RootItemSnapshot> Roots,
        DateTime CreatedUtc);

    private sealed record RootItemSnapshot(
        Guid RootItemId,
        IReadOnlyList<ItemUserStateSnapshot> States);

    private sealed record ItemUserStateSnapshot(
        Guid ItemId,
        long PlaybackPositionTicks,
        bool Played,
        int PlayCount,
        DateTime? LastPlayedDate);
}
