using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

/// <summary>
/// Persisted configuration for one Virtual TV channel.
/// </summary>
public sealed class ChannelConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int Number { get; set; }
    public string ChannelType { get; set; } = "Series";
    public string ContentMode { get; set; } = "Sequential";
    public string SchedulingMethod { get; set; } = "RepeatingSchedule";
    public bool VisibleToAllUsers { get; set; } = true;
    public List<string> VisibleUserIds { get; set; } = new();
    public string OwnerUserId { get; set; } = string.Empty;
    public bool Is24Hours { get; set; }
    public string OnAirStart { get; set; } = "07:00";
    public string OffAirStart { get; set; } = "02:00";
    public int SmartRotationMonths { get; set; } = 2;
    public int DynamicBlockMinutes { get; set; } = 30;
    public List<ChannelContentConfiguration> Content { get; set; } = new();
    public List<string> ManualSeriesOrder { get; set; } = new();
    public List<string> RepeatingSeriesOrder { get; set; } = new();
    public string SmartTemplateCreatedUtc { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public bool NeedsReconcile { get; set; } = true;
}

/// <summary>
/// One explicitly selected series or movie in a channel.
/// </summary>
public sealed class ChannelContentConfiguration
{
    public string ItemId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public bool AllSeasons { get; set; } = true;
    public List<string> SeasonIds { get; set; } = new();
    public bool IncludeSpecials { get; set; }
    public int ConsecutiveEpisodes { get; set; } = 1;
    public int SortOrder { get; set; }
}

/// <summary>
/// Persisted materialized programme block.
/// </summary>
public sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChannelId { get; set; } = string.Empty;
    public string StartUtc { get; set; } = string.Empty;
    public string EndUtc { get; set; } = string.Empty;
    public string Kind { get; set; } = "Content";
    public string ItemId { get; set; } = string.Empty;
    public string SeriesId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string DynamicRule { get; set; } = string.Empty;
}

/// <summary>
/// Persisted schedule metadata used to keep shuffle decisions stable.
/// </summary>
public sealed class ChannelScheduleState
{
    public string ChannelId { get; set; } = string.Empty;
    public List<ScheduleEntry> Entries { get; set; } = new();
    public List<string> SeriesCycle { get; set; } = new();
    public int SeriesCycleIndex { get; set; }
    public List<ShuffleState> ContentShuffleStates { get; set; } = new();
    public List<SeriesCursorState> SeriesCursors { get; set; } = new();
    public string SmartTemplateCreatedUtc { get; set; } = string.Empty;
    public List<string> SmartSeriesPattern { get; set; } = new();
}

/// <summary>
/// One persisted shuffle-cycle state.
/// </summary>
public sealed class ShuffleState
{
    public string Key { get; set; } = string.Empty;
    public List<string> ItemIds { get; set; } = new();
    public int Index { get; set; }
    public string LastItemId { get; set; } = string.Empty;
}

/// <summary>
/// Sequential cursor for a selected series.
/// </summary>
public sealed class SeriesCursorState
{
    public string SeriesId { get; set; } = string.Empty;
    public int Index { get; set; }
}
