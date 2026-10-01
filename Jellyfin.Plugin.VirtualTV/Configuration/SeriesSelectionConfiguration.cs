using System.Collections.Generic;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

/// <summary>
/// Per-series eligibility options. Missing entries are treated as All Seasons with Specials off
/// so channels created before 1.10 remain backward compatible.
/// </summary>
public sealed class SeriesSelectionConfiguration
{
    public string SeriesId { get; set; } = string.Empty;

    public bool AllSeasons { get; set; } = true;

    public List<int> SelectedSeasonNumbers { get; set; } = new();

    public bool IncludeSpecials { get; set; }

    /// <summary>
    /// Relative selection weight used only by True Random scheduling.
    /// Weight 2 has twice the chance of Weight 1, Weight 3 has three times the chance, etc.
    /// Existing channels default to Weight 1.
    /// </summary>
    public int Weight { get; set; } = 1;
}
