using System;
using System.Collections.Concurrent;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Holds temporary snapshots used to validate that Virtual TV playback can preserve Jellyfin user state.
/// </summary>
public sealed class PlaybackStateProtectionManager
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ConcurrentDictionary<string, PlaybackStateSnapshot> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStateProtectionManager"/> class.
    /// </summary>
    public PlaybackStateProtectionManager(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Captures the current Watched/Resume-related state before a protected playback test starts.
    /// </summary>
    public bool BeginProtection(string sessionId, Guid itemId, Guid userId)
    {
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
            sessionId,
            itemId,
            userId,
            data.PlaybackPositionTicks,
            data.Played,
            data.PlayCount,
            data.LastPlayedDate,
            DateTime.UtcNow);

        return true;
    }

    /// <summary>
    /// Cancels protection for a session.
    /// </summary>
    public void CancelProtection(string sessionId)
    {
        _active.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Restores the captured state after Jellyfin reports playback activity.
    /// </summary>
    public void RestoreIfProtected(PlaybackProgressEventArgs eventArgs, bool clearAfterRestore)
    {
        var session = eventArgs.Session;
        var item = eventArgs.Item;
        if (session is null || item is null)
        {
            return;
        }

        if (!_active.TryGetValue(session.Id, out var snapshot))
        {
            return;
        }

        // Do not let an abandoned test context affect later playback.
        if (DateTime.UtcNow - snapshot.CreatedUtc > TimeSpan.FromHours(1))
        {
            _active.TryRemove(session.Id, out _);
            return;
        }

        if (item.Id != snapshot.ItemId)
        {
            return;
        }

        Restore(snapshot);

        if (clearAfterRestore)
        {
            _active.TryRemove(session.Id, out _);
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
        string SessionId,
        Guid ItemId,
        Guid UserId,
        long PlaybackPositionTicks,
        bool Played,
        int PlayCount,
        DateTime? LastPlayedDate,
        DateTime CreatedUtc);
}
