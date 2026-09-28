using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

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

    /// <summary>
    /// Jellyfin library ids selected to feed this channel.
    /// Series channels only accept TV-show libraries and movie channels only accept movie libraries.
    /// </summary>
    public List<string> SelectedLibraryIds { get; set; } = new();

    /// <summary>
    /// Explicitly selected Series ids for Series channels or Movie ids for Movie channels.
    /// </summary>
    public List<string> SelectedItemIds { get; set; } = new();

    public bool Is24Hours { get; set; }
    public string OnAirStart { get; set; } = "07:00";
    public string OffAirStart { get; set; } = "02:00";
    public int SmartRotationMonths { get; set; } = 2;
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public bool NeedsReconcile { get; set; } = true;
    public string ScheduleGeneratedUtc { get; set; } = string.Empty;
    public string ScheduleEndUtc { get; set; } = string.Empty;
}
