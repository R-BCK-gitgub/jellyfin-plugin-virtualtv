using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
            if (!File.Exists(path))
            {
                return Array.Empty<VirtualTvScheduleEntry>();
            }

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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string GetPath(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Channel id cannot be empty.", nameof(channelId));
        }

        // Current channels use GUID ids, but early development versions created a few
        // browser-generated numeric ids. Keep those channels fully functional instead of
        // forcing a destructive migration. Non-GUID ids are converted to a deterministic,
        // filesystem-safe SHA-256 key.
        if (Guid.TryParse(channelId, out var parsed))
        {
            return Path.Combine(_root, parsed.ToString("N") + ".json");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(channelId));
        var safeKey = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(_root, safeKey + ".json");
    }
}
