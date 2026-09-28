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

    public List<ChannelConfiguration> Channels { get; set; }
}
