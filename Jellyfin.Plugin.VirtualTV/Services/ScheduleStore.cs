using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.VirtualTV.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Persists materialized schedules independently from plugin configuration.
/// </summary>
public sealed class ScheduleStore
{
    private readonly object _gate = new();
    private readonly string _root;
    private readonly ILogger<ScheduleStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public ScheduleStore(IApplicationPaths paths, ILogger<ScheduleStore> logger)
    {
        _root = Path.Combine(paths.DataPath, "virtualtv", "schedules");
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public ChannelScheduleState Load(string channelId)
    {
        lock (_gate)
        {
            var path = GetPath(channelId);
            if (!File.Exists(path))
            {
                return new ChannelScheduleState { ChannelId = channelId };
            }

            try
            {
                var state = JsonSerializer.Deserialize<ChannelScheduleState>(File.ReadAllText(path), _jsonOptions);
                return state ?? new ChannelScheduleState { ChannelId = channelId };
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogError(ex, "Unable to read Virtual TV schedule for channel {ChannelId}", channelId);
                return new ChannelScheduleState { ChannelId = channelId };
            }
        }
    }

    public void Save(ChannelScheduleState state)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var path = GetPath(state.ChannelId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, _jsonOptions));
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
        var safe = Guid.TryParse(channelId, out var parsed)
            ? parsed.ToString("N")
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(channelId))).ToLowerInvariant();

        return Path.Combine(_root, safe + ".json");
    }
}
