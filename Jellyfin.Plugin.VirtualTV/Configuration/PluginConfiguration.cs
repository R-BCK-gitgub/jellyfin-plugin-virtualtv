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
}
