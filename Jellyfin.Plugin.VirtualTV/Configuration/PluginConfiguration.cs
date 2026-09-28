using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

/// <summary>
/// Stores Virtual TV plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        Channels = new List<ChannelConfiguration>();
    }

    public int SchemaVersion { get; set; } = 1;
    public List<ChannelConfiguration> Channels { get; set; }

    // Kept only for the incremental architecture-validation path inherited from v1.0.12.
    public bool ArchitectureLiveTvTestEnabled { get; set; }
    public string ArchitectureLiveTvTestItemId { get; set; } = string.Empty;
    public string ArchitectureLiveTvTestProgramStartUtc { get; set; } = string.Empty;
}
