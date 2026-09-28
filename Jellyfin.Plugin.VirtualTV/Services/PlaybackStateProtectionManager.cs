using System;
using System.Collections.Concurrent;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
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

        var data = _userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return false;
        }

        _active[sessionId] = new PlaybackStateSnapshot(
            itemId,
            userId,
            data.PlaybackPositionTicks,
            data.Played,
            data.PlayCount,
            data.LastPlayedDate,
            DateTime.UtcNow);

        _logger.LogDebug(
            "Virtual TV protected Jellyfin user state for session {SessionId}, item {ItemId}.",
            sessionId,
            itemId);

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

        if (eventArgs.Item.Id != snapshot.ItemId)
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
        var item = _libraryManager.GetItemById(snapshot.ItemId);
        if (user is null || item is null)
        {
            return;
        }

        var data = _userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        data.PlaybackPositionTicks = snapshot.PlaybackPositionTicks;
        data.Played = snapshot.Played;
        data.PlayCount = snapshot.PlayCount;
        data.LastPlayedDate = snapshot.LastPlayedDate;

        _userDataManager.SaveUserData(
            user,
            item,
            data,
            UserDataSaveReason.UpdateUserData,
            CancellationToken.None);
    }

    private sealed record PlaybackStateSnapshot(
        Guid ItemId,
        Guid UserId,
        long PlaybackPositionTicks,
        bool Played,
        int PlayCount,
        DateTime? LastPlayedDate,
        DateTime CreatedUtc);
}
