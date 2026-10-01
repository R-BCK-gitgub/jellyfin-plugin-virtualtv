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
/// Supplies the short, neutral bootstrap clips used while a Virtual TV channel hands off
/// from Jellyfin Live TV to the resolved VOD episode.
///
/// Both MP4 files are embedded in the plugin assembly at build time. On first use after each
/// Jellyfin restart they are extracted into Jellyfin's own cache directory so every client can
/// consume them as ordinary local file-backed media sources. Nothing has to be copied manually.
/// </summary>
public sealed class VirtualTvBootstrapMediaProvider
{
    private const string LoadingResource =
        "Jellyfin.Plugin.VirtualTV.Assets.Bootstrap.virtualtv-loading-10s.mp4";
    private const string BlackResource =
        "Jellyfin.Plugin.VirtualTV.Assets.Bootstrap.virtualtv-bootstrap-black-10s.mp4";

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<VirtualTvBootstrapMediaProvider> _logger;
    private readonly object _gate = new();

    private string? _loadingPath;
    private string? _blackPath;

    public VirtualTvBootstrapMediaProvider(
        IApplicationPaths applicationPaths,
        ILogger<VirtualTvBootstrapMediaProvider> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public MediaSourceInfo CreateLoadingSource()
        => CreateSource(
            ResolveLoadingPath(),
            "virtualtv-bootstrap-loading",
            "Loading Virtual TV...");

    public MediaSourceInfo CreateBlackSource()
        => CreateSource(
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

    private void EnsureExtracted()
    {
        if (_loadingPath is not null && _blackPath is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_loadingPath is not null && _blackPath is not null)
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

    private static MediaSourceInfo CreateSource(
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
            IsRemote = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,

            // This is an already-materialized local MP4, not a tuner resource. Returning it as a
            // ready media source avoids Jellyfin's additional OpenMediaSource/GetChannelStream
            // live-stream handshake before playback can begin.
            RequiresOpening = false,
            RequiresClosing = false,
            RunTimeTicks = TimeSpan.FromSeconds(10).Ticks,
            DefaultAudioStreamIndex = 1,
            DefaultSubtitleStreamIndex = null,
            SupportsProbing = false,
            IsInfiniteStream = false,
            RequiresLooping = false,
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
}
