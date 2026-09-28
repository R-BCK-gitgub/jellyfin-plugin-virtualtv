using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.VirtualTV.Services;

public sealed class VirtualTvScheduleStore
{
    private readonly string _root;
    private readonly object _gate = new();

    public VirtualTvScheduleStore(IApplicationPaths applicationPaths)
    {
        _root = Path.Combine(applicationPaths.DataPath, "virtualtv", "schedules");
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<VirtualTvScheduleEntry> Load(string channelId)
    {
        lock (_gate)
        {
            var path = GetPath(channelId);
            if (!File.Exists(path)) return Array.Empty<VirtualTvScheduleEntry>();
            try
            {
                return JsonSerializer.Deserialize<List<VirtualTvScheduleEntry>>(File.ReadAllText(path)) ?? [];
            }
            catch (JsonException)
            {
                return Array.Empty<VirtualTvScheduleEntry>();
            }
        }
    }

    public void Save(string channelId, IReadOnlyList<VirtualTvScheduleEntry> entries)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var path = GetPath(channelId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, path, true);
        }
    }

    public void Delete(string channelId)
    {
        lock (_gate)
        {
            var path = GetPath(channelId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private string GetPath(string channelId)
    {
        if (!Guid.TryParse(channelId, out var parsed)) throw new ArgumentException("Channel id must be a GUID.", nameof(channelId));
        return Path.Combine(_root, parsed.ToString("N") + ".json");
    }
}
