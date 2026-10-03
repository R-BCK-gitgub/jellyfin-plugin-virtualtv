using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Produces the true linear Standard TV path.
///
/// A Standard TV channel never hands off to a library VOD item. Jellyfin opens one normal
/// infinite Live TV source whose bytes are generated on demand from the persisted concrete
/// schedule. Each reader is aligned to the wall clock, starts the active programme at its
/// live offset, then continues through later schedule entries as one MPEG-TS stream.
///
/// Because only the LiveTvChannel is played by Jellyfin, source episodes/movies never receive
/// watched, resume or Continue Watching updates.
/// </summary>
public sealed class VirtualTvStandardStreamService
{
    private const int OutputFps = 30;

    private readonly IServerApplicationHost _appHost;
    private readonly VirtualTvScheduleStore _scheduleStore;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<VirtualTvStandardStreamService> _logger;

    public VirtualTvStandardStreamService(
        IServerApplicationHost appHost,
        VirtualTvScheduleStore scheduleStore,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        ILogger<VirtualTvStandardStreamService> logger)
    {
        _appHost = appHost;
        _scheduleStore = scheduleStore;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public MediaSourceInfo CreateMenuSource(ChannelConfiguration channel)
    {
        var profile = ResolveVideoProfile(channel.StandardTvResolution);

        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = "h264",
            Profile = "main",
            Width = profile.Width,
            Height = profile.Height,
            RealFrameRate = OutputFps,
            AverageFrameRate = OutputFps,
            BitRate = profile.BitRate,
            IsInterlaced = false,
            PixelFormat = "yuv420p"
        };

        var audio = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = 1,
            Codec = "aac",
            Channels = 2,
            SampleRate = 48000,
            BitRate = 160000
        };

        return new MediaSourceInfo
        {
            Id = "virtualtv-standard-" + channel.Id + "-v1",
            Protocol = MediaProtocol.File,
            Container = "mpegts",
            IsInfiniteStream = true,
            BufferMs = 0,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = false,
            MediaStreams = new[] { video, audio }
        };
    }

    public ILiveStream CreateLiveStream(ChannelConfiguration channel)
    {
        if (!VirtualTvModePolicy.IsStandardTV(channel.PlaybackExperience))
        {
            throw new InvalidOperationException("A Personalized TV channel cannot open the Standard TV stream pipeline.");
        }

        var profile = ResolveVideoProfile(channel.StandardTvResolution);
        var liveId = "virtualtv-standard-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        return new StandardLiveStream(
            channel.Id,
            channel.Name,
            profile,
            uniqueId => new MediaSourceInfo
            {
                Id = liveId,
                Path = _appHost.GetApiUrlForLocalAccess()
                    + "/LiveTv/LiveStreamFiles/"
                    + uniqueId
                    + "/stream.ts",
                Protocol = MediaProtocol.Http,
                Container = "mpegts",
                IsInfiniteStream = true,
                BufferMs = 0,
                RequiresOpening = false,
                RequiresClosing = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
                MediaStreams = Array.Empty<MediaStream>()
            },
            _scheduleStore,
            _libraryManager,
            _mediaSourceManager,
            _mediaEncoder,
            _logger);
    }

    private static StandardTvVideoProfile ResolveVideoProfile(int resolution)
        => VirtualTvModePolicy.NormalizeStandardTvResolution(resolution) switch
        {
            480 => new StandardTvVideoProfile(854, 480, 2_000_000, "2M", "4M"),
            1080 => new StandardTvVideoProfile(1920, 1080, 8_000_000, "8M", "16M"),
            _ => new StandardTvVideoProfile(1280, 720, 4_000_000, "4M", "8M")
        };

    private sealed record StandardTvVideoProfile(
        int Width,
        int Height,
        int BitRate,
        string MaxRate,
        string BufferSize);

    private sealed class StandardLiveStream : ILiveStream, IDirectStreamProvider
    {
        private readonly string _channelId;
        private readonly string _channelName;
        private readonly StandardTvVideoProfile _videoProfile;
        private readonly VirtualTvScheduleStore _scheduleStore;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _streamGate = new();
        private readonly SemaphoreSlim _readGate = new(1, 1);
        private StandardBroadcastStream? _broadcast;
        private int _closed;

        public StandardLiveStream(
            string channelId,
            string channelName,
            StandardTvVideoProfile videoProfile,
            Func<string, MediaSourceInfo> buildSource,
            VirtualTvScheduleStore scheduleStore,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            ILogger logger)
        {
            _channelId = channelId;
            _channelName = channelName;
            _videoProfile = videoProfile;
            _scheduleStore = scheduleStore;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _logger = logger;
            UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            MediaSource = buildSource(UniqueId);
        }

        public int ConsumerCount { get; set; } = 1;
        public string OriginalStreamId { get; set; } = string.Empty;
        public string TunerHostId => string.Empty;
        public bool EnableStreamSharing => true;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }

        public Task Open(CancellationToken openCancellationToken)
        {
            openCancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _lifetime.Cancel();

                lock (_streamGate)
                {
                    _broadcast?.Dispose();
                    _broadcast = null;
                }

                _logger.LogInformation(
                    "Virtual TV Standard TV stream closed for channel {ChannelName}.",
                    _channelName);
            }

            return Task.CompletedTask;
        }

        public Stream GetStream()
        {
            if (_lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(StandardLiveStream));
            }

            StandardBroadcastStream broadcast;
            lock (_streamGate)
            {
                if (_lifetime.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(StandardLiveStream));
                }

                // Jellyfin can read the direct stream once while probing and again for the
                // actual player. Recreating the producer on each GetStream caused the first
                // ~1-2 seconds to replay. One producer per tune-in keeps the timeline continuous.
                _broadcast ??= new StandardBroadcastStream(
                    _channelId,
                    _channelName,
                    _videoProfile,
                    _scheduleStore,
                    _libraryManager,
                    _mediaSourceManager,
                    _mediaEncoder,
                    _logger,
                    _lifetime.Token);

                broadcast = _broadcast;
            }

            _logger.LogInformation(
                "Virtual TV Standard TV reader attached to the existing tune-in stream for channel {ChannelName}.",
                _channelName);

            return new SharedBroadcastReader(broadcast, _readGate);
        }

        public void Dispose()
        {
            _ = Close();
            _readGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class SharedBroadcastReader : Stream
    {
        private readonly Stream _inner;
        private readonly SemaphoreSlim _readGate;

        public SharedBroadcastReader(Stream inner, SemaphoreSlim readGate)
        {
            _inner = inner;
            _readGate = readGate;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _readGate.Wait();
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            finally
            {
                _readGate.Release();
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _readGate.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Non-owning: the parent ILiveStream owns the shared producer lifetime.
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StandardBroadcastStream : Stream
    {
        private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "ttml"
        };

        private static readonly HashSet<string> BitmapSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvb_subtitle"
        };

        private readonly string _channelId;
        private readonly string _channelName;
        private readonly StandardTvVideoProfile _videoProfile;
        private readonly VirtualTvScheduleStore _scheduleStore;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime;
        private readonly DateTime _readerStartedUtc = DateTime.UtcNow;
        private readonly HashSet<string> _disableSubtitlesForEntry = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failedEntries = new(StringComparer.Ordinal);

        private Process? _process;
        private Stream? _stdout;
        private Task<string>? _stderrTask;
        private string _currentEntryId = string.Empty;
        private DateTime _currentExpectedEndUtc;
        private bool _currentUsedSubtitles;
        private long _bytesFromCurrent;
        private bool _disposed;

        public StandardBroadcastStream(
            string channelId,
            string channelName,
            StandardTvVideoProfile videoProfile,
            VirtualTvScheduleStore scheduleStore,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder,
            ILogger logger,
            CancellationToken parentToken)
        {
            _channelId = channelId;
            _channelName = channelName;
            _videoProfile = videoProfile;
            _scheduleStore = scheduleStore;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
            _logger = logger;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                cancellationToken);
            var token = linked.Token;

            while (!token.IsCancellationRequested)
            {
                EnsureProducer();

                if (_stdout is null)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                int read;
                try
                {
                    read = await _stdout.ReadAsync(buffer, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Virtual TV Standard TV reader hit an ffmpeg pipe error on {ChannelName}.",
                        _channelName);
                    read = 0;
                }

                if (read > 0)
                {
                    _bytesFromCurrent += read;
                    return read;
                }

                FinishProducer();
            }

            return 0;
        }

        private void EnsureProducer()
        {
            if (_process is not null && !_process.HasExited && _stdout is not null)
            {
                return;
            }

            FinishProducer();

            var nowUtc = DateTime.UtcNow;
            var schedule = _scheduleStore.Load(_channelId)
                .OrderBy(entry => entry.GetStartUtc())
                .ToList();

            var entry = schedule.FirstOrDefault(item =>
                item.GetStartUtc() <= nowUtc
                && item.GetEndUtc() > nowUtc);

            if (entry is null)
            {
                StartSlate("no-active-entry", nowUtc.AddMinutes(1), "no active schedule entry");
                return;
            }

            _currentEntryId = entry.Id;
            _currentExpectedEndUtc = entry.GetEndUtc();
            _bytesFromCurrent = 0;

            if (entry.IsOffAir || entry.IsContentUnavailable || entry.IsScheduleUnavailable || entry.IsDynamicBlock)
            {
                StartSlate(
                    entry.Id,
                    entry.GetEndUtc(),
                    entry.IsOffAir
                        ? "off air"
                        : entry.IsDynamicBlock
                            ? "dynamic schedule requires regeneration for Standard TV"
                            : "content unavailable");
                return;
            }

            if (_failedEntries.Contains(entry.Id))
            {
                StartSlate(entry.Id, entry.GetEndUtc(), "programme source previously failed");
                return;
            }

            if (!Guid.TryParse(entry.SourceItemId, out var itemId))
            {
                _failedEntries.Add(entry.Id);
                StartSlate(entry.Id, entry.GetEndUtc(), "invalid scheduled item id");
                return;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _failedEntries.Add(entry.Id);
                StartSlate(entry.Id, entry.GetEndUtc(), "scheduled item no longer exists");
                return;
            }

            var source = ResolveMediaSource(item);
            var path = source?.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = item.Path;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                _failedEntries.Add(entry.Id);
                StartSlate(entry.Id, entry.GetEndUtc(), "scheduled item has no playable path");
                return;
            }

            var offset = nowUtc - entry.GetStartUtc();
            if (offset < TimeSpan.Zero)
            {
                offset = TimeSpan.Zero;
            }

            var remaining = entry.GetEndUtc() - nowUtc;
            if (remaining <= TimeSpan.Zero)
            {
                StartSlate(entry.Id, nowUtc.AddSeconds(1), "schedule boundary");
                return;
            }

            var allowSubtitles = !_disableSubtitlesForEntry.Contains(entry.Id);
            var subtitle = allowSubtitles && source is not null
                ? SelectEnglishSubtitle(source)
                : null;

            try
            {
                StartMedia(item, source, path, entry.Id, offset, remaining, subtitle);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Virtual TV Standard TV could not start {ItemName} on {ChannelName}; filling the remaining slot.",
                    item.Name,
                    _channelName);

                _failedEntries.Add(entry.Id);
                StartSlate(entry.Id, entry.GetEndUtc(), "ffmpeg start failure");
            }
        }

        private MediaSourceInfo? ResolveMediaSource(BaseItem item)
        {
            try
            {
                var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
                return sources.FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.Path))
                    ?? sources.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Virtual TV Standard TV could not resolve static media source metadata for {ItemName}; using the item path.",
                    item.Name);
                return null;
            }
        }

        private void StartMedia(
            BaseItem item,
            MediaSourceInfo? source,
            string path,
            string entryId,
            TimeSpan offset,
            TimeSpan remaining,
            SubtitleChoice? subtitle)
        {
            _ = entryId;
            var ffmpeg = _mediaEncoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                throw new InvalidOperationException("Jellyfin ffmpeg path is not available.");
            }

            var start = NewFfmpegStartInfo(ffmpeg);
            Add(start, "-hide_banner", "-loglevel", "warning", "-nostdin");
            Add(start, "-ss", FormatSeconds(offset), "-re", "-i", path);

            var audioOrdinal = GetPreferredAudioOrdinal(source);
            var hasAudio = audioOrdinal.HasValue;

            if (!hasAudio)
            {
                Add(start, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo");
            }

            Add(start, "-t", FormatSeconds(remaining));

            _currentUsedSubtitles = subtitle is not null;

            if (subtitle is not null && subtitle.IsBitmap && !subtitle.Stream.IsExternal)
            {
                var filter =
                    "[0:v:0][0:s:" + subtitle.SubtitleOrdinal.ToString(CultureInfo.InvariantCulture) + "]"
                    + "overlay=eof_action=pass:shortest=0,"
                    + ScaleFilter()
                    + "[vout]";
                Add(start, "-filter_complex", filter, "-map", "[vout]");
            }
            else
            {
                var videoFilter = subtitle is not null
                    ? TextSubtitleFilter(path, offset, subtitle)
                    : ScaleFilter();

                Add(start, "-vf", videoFilter, "-map", "0:v:0");
            }

            if (hasAudio)
            {
                Add(
                    start,
                    "-map",
                    "0:a:" + audioOrdinal!.Value.ToString(CultureInfo.InvariantCulture) + "?");
            }
            else
            {
                Add(start, "-map", "1:a:0");
            }

            Add(
                start,
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-profile:v", "main",
                "-level:v", "4.0",
                "-pix_fmt", "yuv420p",
                "-crf", "21",
                "-maxrate", _videoProfile.MaxRate,
                "-bufsize", _videoProfile.BufferSize,
                "-g", "60",
                "-keyint_min", "60",
                "-sc_threshold", "0",
                "-c:a", "aac",
                "-b:a", "160k",
                "-ac", "2",
                "-ar", "48000",
                "-af", "aresample=async=1:first_pts=0",
                "-mpegts_flags", "+resend_headers",
                "-muxdelay", "0",
                "-muxpreload", "0",
                "-output_ts_offset", FormatSeconds(DateTime.UtcNow - _readerStartedUtc),
                "-f", "mpegts",
                "pipe:1");

            StartProcess(start);

            _logger.LogInformation(
                "Virtual TV Standard TV {ChannelName}: airing {ItemName} at live offset {Offset}; English subtitles {SubtitleState}.",
                _channelName,
                item.Name,
                offset,
                subtitle is null ? "not available" : "burned in");
        }

        private void StartSlate(string entryId, DateTime endUtc, string reason)
        {
            var ffmpeg = _mediaEncoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                throw new InvalidOperationException("Jellyfin ffmpeg path is not available.");
            }

            var now = DateTime.UtcNow;
            var remaining = endUtc - now;
            if (remaining <= TimeSpan.Zero)
            {
                remaining = TimeSpan.FromSeconds(1);
            }

            if (remaining > TimeSpan.FromMinutes(5))
            {
                remaining = TimeSpan.FromMinutes(5);
            }

            _currentEntryId = entryId;
            _currentExpectedEndUtc = now.Add(remaining);
            _currentUsedSubtitles = false;
            _bytesFromCurrent = 0;

            var start = NewFfmpegStartInfo(ffmpeg);
            Add(
                start,
                "-hide_banner", "-loglevel", "warning", "-nostdin",
                "-re",
                "-f", "lavfi",
                "-i", $"color=c=black:s={_videoProfile.Width}x{_videoProfile.Height}:r={OutputFps}",
                "-f", "lavfi",
                "-i", "anullsrc=r=48000:cl=stereo",
                "-t", FormatSeconds(remaining),
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-profile:v", "main",
                "-level:v", "4.0",
                "-pix_fmt", "yuv420p",
                "-crf", "35",
                "-g", "60",
                "-keyint_min", "60",
                "-sc_threshold", "0",
                "-c:a", "aac",
                "-b:a", "32k",
                "-ac", "2",
                "-ar", "48000",
                "-mpegts_flags", "+resend_headers",
                "-muxdelay", "0",
                "-muxpreload", "0",
                "-output_ts_offset", FormatSeconds(DateTime.UtcNow - _readerStartedUtc),
                "-f", "mpegts",
                "pipe:1");

            StartProcess(start);

            _logger.LogInformation(
                "Virtual TV Standard TV {ChannelName}: serving neutral live slate ({Reason}).",
                _channelName,
                reason);
        }

        private void StartProcess(ProcessStartInfo start)
        {
            _process = Process.Start(start)
                ?? throw new InvalidOperationException("Unable to start Jellyfin ffmpeg.");
            _stdout = _process.StandardOutput.BaseStream;
            _stderrTask = _process.StandardError.ReadToEndAsync();
        }

        private void FinishProducer()
        {
            if (_process is null)
            {
                _stdout = null;
                return;
            }

            var process = _process;
            var entryId = _currentEntryId;
            var expectedEnd = _currentExpectedEndUtc;
            var bytes = _bytesFromCurrent;
            var usedSubtitles = _currentUsedSubtitles;

            _process = null;
            _stdout = null;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }

            string stderr = string.Empty;
            try
            {
                if (_stderrTask is not null && _stderrTask.IsCompletedSuccessfully)
                {
                    stderr = _stderrTask.Result.Trim();
                }
            }
            catch
            {
            }

            var exitCode = process.HasExited ? process.ExitCode : -1;
            process.Dispose();
            _stderrTask = null;

            if (_lifetime.IsCancellationRequested || string.IsNullOrWhiteSpace(entryId))
            {
                return;
            }

            var early = DateTime.UtcNow < expectedEnd.Subtract(TimeSpan.FromSeconds(2));

            if (early && usedSubtitles && bytes == 0 && !_disableSubtitlesForEntry.Contains(entryId))
            {
                _disableSubtitlesForEntry.Add(entryId);
                _logger.LogWarning(
                    "Virtual TV Standard TV {ChannelName}: English subtitle burn-in failed before output; retrying the active programme without subtitles. ffmpeg: {FfmpegError}",
                    _channelName,
                    Tail(stderr));
                return;
            }

            if (early)
            {
                _failedEntries.Add(entryId);
                _logger.LogWarning(
                    "Virtual TV Standard TV {ChannelName}: programme producer ended early (exit {ExitCode}, bytes {Bytes}). The remaining slot will use the neutral live slate. ffmpeg: {FfmpegError}",
                    _channelName,
                    exitCode,
                    bytes,
                    Tail(stderr));
            }
        }

        private static ProcessStartInfo NewFfmpegStartInfo(string ffmpeg)
            => new()
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };

        private static void Add(ProcessStartInfo info, params string[] arguments)
        {
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
        }

        private string ScaleFilter()
            => $"scale={_videoProfile.Width}:{_videoProfile.Height}:force_original_aspect_ratio=decrease,"
                + $"pad={_videoProfile.Width}:{_videoProfile.Height}:(ow-iw)/2:(oh-ih)/2:black,"
                + $"setsar=1,fps={OutputFps},format=yuv420p";

        private string TextSubtitleFilter(
            string mediaPath,
            TimeSpan offset,
            SubtitleChoice subtitle)
        {
            var subtitlePath = subtitle.Stream.IsExternal && !string.IsNullOrWhiteSpace(subtitle.Stream.Path)
                ? subtitle.Stream.Path!
                : mediaPath;

            var filter = "subtitles=filename='" + EscapeFilterPath(subtitlePath) + "'";
            if (!subtitle.Stream.IsExternal)
            {
                filter += ":si=" + subtitle.SubtitleOrdinal.ToString(CultureInfo.InvariantCulture);
            }

            if (offset > TimeSpan.Zero)
            {
                var seconds = FormatSeconds(offset);
                return "setpts=PTS+" + seconds + "/TB,"
                    + filter
                    + ",setpts=PTS-" + seconds + "/TB,"
                    + ScaleFilter();
            }

            return filter + "," + ScaleFilter();
        }

        private static string EscapeFilterPath(string value)
            => value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal)
                .Replace(":", "\\:", StringComparison.Ordinal)
                .Replace(",", "\\,", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);

        private static int? GetPreferredAudioOrdinal(MediaSourceInfo? source)
        {
            if (source is null)
            {
                return 0;
            }

            var audio = source.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Audio)
                .OrderBy(stream => stream.Index)
                .ToList();

            if (audio.Count == 0)
            {
                return null;
            }

            var selected = source.DefaultAudioStreamIndex.HasValue
                ? audio.FirstOrDefault(stream => stream.Index == source.DefaultAudioStreamIndex.Value)
                : null;
            selected ??= audio.FirstOrDefault(stream => stream.IsDefault);
            selected ??= audio[0];

            return audio.IndexOf(selected);
        }

        private static SubtitleChoice? SelectEnglishSubtitle(MediaSourceInfo source)
        {
            var subtitles = source.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Subtitle && IsEnglish(stream.Language))
                .OrderBy(stream => stream.IsForced)
                .ThenByDescending(stream => stream.IsDefault)
                .ThenBy(stream => stream.Index)
                .ToList();

            if (subtitles.Count == 0)
            {
                return null;
            }

            var allSubtitles = source.MediaStreams
                .Where(stream => stream.Type == MediaStreamType.Subtitle)
                .OrderBy(stream => stream.Index)
                .ToList();

            foreach (var stream in subtitles)
            {
                var codec = stream.Codec ?? string.Empty;
                var isText = TextSubtitleCodecs.Contains(codec)
                    || (stream.IsExternal && !BitmapSubtitleCodecs.Contains(codec));
                var isBitmap = BitmapSubtitleCodecs.Contains(codec);

                if (!isText && (!isBitmap || stream.IsExternal))
                {
                    continue;
                }

                var ordinal = allSubtitles.IndexOf(stream);
                if (ordinal < 0)
                {
                    continue;
                }

                return new SubtitleChoice(stream, ordinal, isBitmap);
            }

            return null;
        }

        private static bool IsEnglish(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return false;
            }

            var normalized = language.Trim().ToLowerInvariant();
            return normalized == "eng"
                || normalized == "english"
                || normalized == "en"
                || normalized.StartsWith("en-", StringComparison.Ordinal);
        }

        private static string FormatSeconds(TimeSpan value)
            => Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

        private static string Tail(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(no ffmpeg diagnostic output)";
            }

            const int max = 1200;
            return value.Length <= max ? value : value[^max..];
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            _disposed = true;

            if (disposing)
            {
                _lifetime.Cancel();
                FinishProducer();
                _lifetime.Dispose();
            }

            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private sealed record SubtitleChoice(
            MediaStream Stream,
            int SubtitleOrdinal,
            bool IsBitmap);
    }
}
