using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Supplies the neutral bootstrap media used while a Virtual TV channel hands off
/// from Jellyfin Live TV to one resolved VOD episode/movie.
///
/// The user-visible 10-second MP4s are still embedded for compatibility/fallback purposes.
/// Live TV itself uses a separate 60-second MPEG-TS loading asset. Jellyfin treats plugin
/// Live TV sources as infinite streams; MPEG-TS is a stream-oriented container and the
/// 60-second runway is intentionally much longer than the five-second confirmed-playback
/// buffer, so Virtual TV always hands off before the source reaches EOF.
/// </summary>
public sealed class VirtualTvBootstrapMediaProvider
{
    private const string LoadingResource =
        "Jellyfin.Plugin.VirtualTV.Assets.Bootstrap.virtualtv-loading-10s.mp4";
    private const string BlackResource =
        "Jellyfin.Plugin.VirtualTV.Assets.Bootstrap.virtualtv-bootstrap-black-10s.mp4";
    private const string LiveLoadingResource =
        "Jellyfin.Plugin.VirtualTV.Assets.Bootstrap.virtualtv-loading-live-60s.ts";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<VirtualTvBootstrapMediaProvider> _logger;
    private readonly object _gate = new();

    private string? _loadingPath;
    private string? _blackPath;
    private string? _liveLoadingPath;

    public VirtualTvBootstrapMediaProvider(
        IApplicationPaths applicationPaths,
        ILogger<VirtualTvBootstrapMediaProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Creates the universal Live TV bootstrap source.
    ///
    /// Do not direct-play the finite MP4 as an "infinite" Live TV source. Instead Jellyfin
    /// receives a real-time MPEG-TS input, is allowed to direct-stream/remux it, and may
    /// transcode when the client requires it. ReadAtNativeFramerate keeps the local file
    /// advancing at wall-clock speed instead of being consumed as fast as disk allows.
    /// </summary>
    public MediaSourceInfo CreateLoadingSource()
        => new()
        {
            Id = "virtualtv-bootstrap-loading-live",
            Path = ResolveLiveLoadingPath(),
            Name = "Loading Virtual TV...",
            Container = "mpegts",
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            IsRemote = false,

            // A browser/webOS client must never be handed the finite local file as a normal
            // VOD DirectPlay source. Jellyfin may remux/direct-stream the TS or transcode it.
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            UseMostCompatibleTranscodingProfile = true,

            // No extra tuner/open handshake is required: this is already materialized media.
            RequiresOpening = false,
            RequiresClosing = false,

            // The Jellyfin Live TV provider normalizes channel sources as infinite. Keep our
            // declaration consistent, but provide a 60s physical runway and always switch after
            // confirmed progress + five seconds.
            IsInfiniteStream = true,
            RunTimeTicks = null,
            RequiresLooping = false,
            ReadAtNativeFramerate = true,
            SupportsProbing = false,
            DefaultAudioStreamIndex = 1,
            DefaultSubtitleStreamIndex = null,

            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Index = 0,
                    Type = MediaStreamType.Video,
                    Codec = "h264",
                    Profile = "main",
                    Width = 1280,
                    Height = 720,
                    AverageFrameRate = 24,
                    RealFrameRate = 24,
                    IsAVC = true,
                    IsDefault = true
                },
                new()
                {
                    Index = 1,
                    Type = MediaStreamType.Audio,
                    Codec = "aac",
                    Profile = "lc",
                    Channels = 2,
                    ChannelLayout = "stereo",
                    SampleRate = 48000,
                    IsDefault = true
                }
            }
        };

    public MediaSourceInfo CreateBlackSource()
        => CreateFiniteMp4Source(
            ResolveBlackPath(),
            "virtualtv-bootstrap-black",
            "Virtual TV");

    public string ResolveLoadingPath()
    {
        EnsureExtracted();
        return _loadingPath!;
    }

    public string ResolveBlackPath()
    {
        EnsureExtracted();
        return _blackPath!;
    }

    public string ResolveLiveLoadingPath()
    {
        EnsureExtracted();
        return _liveLoadingPath!;
    }

    private void EnsureExtracted()
    {
        if (_loadingPath is not null && _blackPath is not null && _liveLoadingPath is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_loadingPath is not null && _blackPath is not null && _liveLoadingPath is not null)
            {
                return;
            }

            var directory = Path.Combine(_applicationPaths.CachePath, "virtualtv", "bootstrap");
            Directory.CreateDirectory(directory);

            _loadingPath = Extract(
                LoadingResource,
                Path.Combine(directory, "virtualtv-loading-10s.mp4"));

            _blackPath = Extract(
                BlackResource,
                Path.Combine(directory, "virtualtv-bootstrap-black-10s.mp4"));

            _liveLoadingPath = Extract(
                LiveLoadingResource,
                Path.Combine(directory, "virtualtv-loading-live-60s.ts"));

            _logger.LogInformation(
                "Virtual TV extracted embedded bootstrap media to {BootstrapDirectory}.",
                directory);
        }
    }

    private static string Extract(string resourceName, string destination)
    {
        var assembly = typeof(VirtualTvBootstrapMediaProvider).Assembly;
        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Virtual TV bootstrap resource '{resourceName}' was not found.");

        var temporary = destination + ".tmp";
        using (var target = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            source.CopyTo(target);
            target.Flush(true);
        }

        File.Move(temporary, destination, overwrite: true);
        return destination;
    }

    private static MediaSourceInfo CreateFiniteMp4Source(
        string path,
        string id,
        string name)
        => new()
        {
            Id = id,
            Path = path,
            Name = name,
            Container = "mp4",
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            IsRemote = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RequiresOpening = false,
            RequiresClosing = false,
            RunTimeTicks = TimeSpan.FromSeconds(10).Ticks,
            SupportsProbing = false,
            IsInfiniteStream = false,
            RequiresLooping = false
        };
}
