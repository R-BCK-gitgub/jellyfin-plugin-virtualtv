using System;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Concrete library item. Dynamic series blocks carry only a bootstrap episode here;
    /// Dynamic movie blocks carry the materialized movie shown in the Guide.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    public string SourceSeriesId { get; set; } = string.Empty;

    public string PlaybackMode { get; set; } = string.Empty;

    public bool IsDynamicBlock { get; set; }

    /// <summary>
    /// Empty for materialized playback, "Series" for watched-dependent series blocks and
    /// "Movie" for Movie Random Unwatched blocks.
    /// </summary>
    public string DynamicKind { get; set; } = string.Empty;

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
    public bool IsContentUnavailable { get; set; }
    public bool IsScheduleUnavailable { get; set; }

    public string StartUtc { get; set; } = string.Empty;
    public string EndUtc { get; set; } = string.Empty;

    public DateTime GetStartUtc()
        => DateTime.Parse(StartUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public DateTime GetEndUtc()
        => DateTime.Parse(EndUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
