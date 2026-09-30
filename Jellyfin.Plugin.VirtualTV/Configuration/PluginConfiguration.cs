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
        KnownInternalChannelIds = new List<string>();
    }

    public List<ChannelConfiguration> Channels { get; set; }

    /// <summary>
    /// Jellyfin internal ids previously managed by Virtual TV. Used only to remove stale
    /// BlockedChannels entries cleanly after a Virtual TV channel is deleted.
    /// </summary>
    public List<string> KnownInternalChannelIds { get; set; }
}
