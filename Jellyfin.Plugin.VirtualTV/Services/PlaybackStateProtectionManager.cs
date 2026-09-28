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
/// Preserves the underlying library item's watched/resume state while Virtual TV temporarily
/// uses normal Jellyfin item playback to obtain reliable start-at-offset behavior on clients.
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
    /// Captures watched/resume-related values before a Virtual TV handoff starts.
    /// If the same session was already protected, its previous snapshot is restored first.
    /// Alternate versions are included because Jellyfin can propagate completion between versions.
    /// </summary>
    public bool BeginProtection(string sessionId, Guid itemId, Guid userId)
    {
        CancelProtection(sessionId, restore: true);

        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || item is null)
        {
            return false;
        }

        IReadOnlyList<BaseItem> protectedItems = item is Video video
            ? video.GetAllVersions().Cast<BaseItem>().ToArray()
            : [item];

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

        if (states.Count == 0)
        {
            return false;
        }

        _active[sessionId] = new PlaybackStateSnapshot(
            itemId,
            userId,
            states,
            DateTime.UtcNow);

        _logger.LogDebug(
            "Virtual TV protected Jellyfin user state for session {SessionId}, item {ItemId}, {StateCount} version(s).",
            sessionId,
            itemId,
            states.Count);

        return true;
    }

    /// <summary>
    /// Restores state when a protected item's playback event is observed.
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

        if (eventArgs.Item.Id != snapshot.RootItemId)
        {
            return;
        }

        Restore(snapshot);

        if (clearAfterRestore)
        {
            _active.TryRemove(eventArgs.Session.Id, out _);
        }
    }

    /// <summary>
    /// Cancels a session protection context, optionally restoring the snapshot first.
    /// </summary>
    public void CancelProtection(string sessionId, bool restore)
    {
        if (!_active.TryRemove(sessionId, out var snapshot))
        {
            return;
        }

        if (restore)
        {
            Restore(snapshot);
        }
    }

    private void Restore(PlaybackStateSnapshot snapshot)
    {
        var user = _userManager.GetUserById(snapshot.UserId);
        if (user is null)
        {
            return;
        }

        foreach (var state in snapshot.States)
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
        Guid RootItemId,
        Guid UserId,
        IReadOnlyList<ItemUserStateSnapshot> States,
        DateTime CreatedUtc);

    private sealed record ItemUserStateSnapshot(
        Guid ItemId,
        long PlaybackPositionTicks,
        bool Played,
        int PlayCount,
        DateTime? LastPlayedDate);
}
