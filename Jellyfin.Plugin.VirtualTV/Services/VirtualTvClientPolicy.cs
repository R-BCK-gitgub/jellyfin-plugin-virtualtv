using System;

namespace Jellyfin.Plugin.VirtualTV.Services;

/// <summary>
/// Central client-name compatibility rules for Jellyfin clients whose reported names changed
/// between releases. Keep TV detection intentionally narrow so Jellyfin for Android phones/tablets
/// are never treated as Android TV.
/// </summary>
public static class VirtualTvClientPolicy
{
    private const string LegacyAndroidTvPrefix = "Jellyfin Android TV";
    private const string CurrentAndroidTvPrefix = "Jellyfin for Android TV";
    private const string LegacyAndroidMobilePrefix = "Jellyfin Android";
    private const string CurrentAndroidMobilePrefix = "Jellyfin for Android";

    public static bool IsAndroidTv(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return false;
        }

        return clientName.StartsWith(LegacyAndroidTvPrefix, StringComparison.OrdinalIgnoreCase)
            || clientName.StartsWith(CurrentAndroidTvPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true only for the Jellyfin Android phone/tablet app, never Android TV.
    /// Both the current and historical client names are accepted because persisted sessions
    /// can survive app/server upgrades.
    /// </summary>
    public static bool IsAndroidMobile(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName) || IsAndroidTv(clientName))
        {
            return false;
        }

        return clientName.StartsWith(CurrentAndroidMobilePrefix, StringComparison.OrdinalIgnoreCase)
            || clientName.StartsWith(LegacyAndroidMobilePrefix, StringComparison.OrdinalIgnoreCase);
    }
}
