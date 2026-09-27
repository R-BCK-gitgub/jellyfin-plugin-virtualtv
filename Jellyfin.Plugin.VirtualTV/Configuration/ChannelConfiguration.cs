using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

/// <summary>
/// Persisted configuration for one Virtual TV channel.
/// </summary>
public sealed class ChannelConfiguration
{
    /// <summary>
    /// Gets or sets the stable channel identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel number.
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Gets or sets the immutable channel type: Series or Movies.
    /// </summary>
    public string ChannelType { get; set; } = "Series";

    /// <summary>
    /// Gets or sets the immutable content mode.
    /// </summary>
    public string ContentMode { get; set; } = "Sequential";

    /// <summary>
    /// Gets or sets the immutable scheduling method for series channels.
    /// </summary>
    public string SchedulingMethod { get; set; } = "RepeatingSchedule";

    /// <summary>
    /// Gets or sets a value indicating whether every Jellyfin user can see the channel.
    /// </summary>
    public bool VisibleToAllUsers { get; set; } = true;

    /// <summary>
    /// Gets or sets the Jellyfin user ids allowed to see the channel when visibility is restricted.
    /// </summary>
    public List<string> VisibleUserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the channel broadcasts 24 hours.
    /// </summary>
    public bool Is24Hours { get; set; }

    /// <summary>
    /// Gets or sets the daily on-air start time using HH:mm.
    /// </summary>
    public string OnAirStart { get; set; } = "07:00";

    /// <summary>
    /// Gets or sets the daily off-air start time using HH:mm. Values earlier than OnAirStart are interpreted as next day.
    /// </summary>
    public string OffAirStart { get; set; } = "02:00";

    /// <summary>
    /// Gets or sets the Smart Schedule rotation period in months.
    /// </summary>
    public int SmartRotationMonths { get; set; } = 2;

    /// <summary>
    /// Gets or sets the UTC creation timestamp.
    /// </summary>
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");

    /// <summary>
    /// Gets or sets a value indicating whether schedule-affecting changes are waiting for reconciliation.
    /// </summary>
    public bool NeedsReconcile { get; set; } = true;
}
