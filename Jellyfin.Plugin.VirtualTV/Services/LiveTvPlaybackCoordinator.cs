using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class LiveTvPlaybackCoordinator
{
    private static readonly TimeSpan DuplicateGuard = TimeSpan.FromSeconds(15);
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LiveTvPlaybackCoordinator> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _recentlyHandled = new(StringComparer.Ordinal);

    public LiveTvPlaybackCoordinator(ISessionManager sessionManager, ILibraryManager libraryManager, ILogger<LiveTvPlaybackCoordinator> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task HandlePlaybackStartAsync(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Item is not LiveTvChannel channel
            || !string.Equals(channel.ServiceName, VirtualTvLiveTvService.ServiceName, StringComparison.OrdinalIgnoreCase))
            return;

        var session = eventArgs.Session;
        if (session is null || string.IsNullOrWhiteSpace(session.Id)) return;

        var now = DateTime.UtcNow;
        var currentProgram = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            ChannelIds = [channel.Id],
            MaxStartDate = now,
            MinEndDate = now,
            Limit = 1
        })
        .OfType<LiveTvProgram>()
        .OrderByDescending(program => program.StartDate)
        .FirstOrDefault();

        if (currentProgram is null) return;

        var targetTicks = Math.Max(0, (now - currentProgram.StartDate).Ticks);
        if (eventArgs.PlaybackPositionTicks >= targetTicks - TimeSpan.FromSeconds(5).Ticks) return;

        if (_recentlyHandled.TryGetValue(session.Id, out var lastHandled) && now - lastHandled < DuplicateGuard) return;
        _recentlyHandled[session.Id] = now;

        _logger.LogInformation(
            "Virtual TV Live seek for session {SessionId}: channel {ChannelName}, programme {ProgramName}, target {TargetSeconds} seconds.",
            session.Id, channel.Name, currentProgram.Name, TimeSpan.FromTicks(targetTicks).TotalSeconds);

        await Task.Delay(500).ConfigureAwait(false);
        await _sessionManager.SendPlaystateCommand(
            session.Id,
            session.Id,
            new PlaystateRequest { Command = PlaystateCommand.Seek, SeekPositionTicks = targetTicks },
            CancellationToken.None).ConfigureAwait(false);
    }
}
