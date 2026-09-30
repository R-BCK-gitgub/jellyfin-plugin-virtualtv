using System;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Concrete library item used by the schedule. For dynamic series blocks this is a
    /// bootstrap episode used only to satisfy Jellyfin's native Live TV media-source opening;
    /// the user-specific episode is resolved by the playback coordinator.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Source Series id for dynamic series blocks.
    /// </summary>
    public string SourceSeriesId { get; set; } = string.Empty;

    public string PlaybackMode { get; set; } = string.Empty;
    public bool IsDynamicBlock { get; set; }
    public int BlockMinutes { get; set; }

    public string Name { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public int? ProductionYear { get; set; }
    public string PremiereDateUtc { get; set; } = string.Empty;
    public bool IsMovie { get; set; }
    public bool IsOffAir { get; set; }
    public string StartUtc { get; set; } = string.Empty;
    public string EndUtc { get; set; } = string.Empty;

    public DateTime GetStartUtc()
        => DateTime.Parse(StartUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public DateTime GetEndUtc()
        => DateTime.Parse(EndUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
