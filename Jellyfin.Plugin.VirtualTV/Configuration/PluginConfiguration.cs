using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

/// <summary>
/// Stores Virtual TV plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Channels = new List<ChannelConfiguration>();
    }

    /// <summary>
    /// Gets or sets the persisted Virtual TV channels.
    /// </summary>
    public List<ChannelConfiguration> Channels { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the temporary Live TV architecture test channel is enabled.
    /// </summary>
    public bool ArchitectureLiveTvTestEnabled { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin media item used as the temporary Live TV architecture test source.
    /// </summary>
    public string ArchitectureLiveTvTestItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stable UTC start time of the temporary architecture-test programme.
    /// </summary>
    public string ArchitectureLiveTvTestProgramStartUtc { get; set; } = string.Empty;
}
