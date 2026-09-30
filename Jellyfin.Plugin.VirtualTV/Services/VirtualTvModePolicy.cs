using System;

namespace Jellyfin.Plugin.VirtualTV.Services;

public static class VirtualTvModePolicy
{
    public const string Sequential = "Sequential";
    public const string NextUnwatched = "NextUnwatched";
    public const string RandomUnwatched = "RandomUnwatched";
    public const string Random = "Random";

    public const string RepeatingOrder = "RepeatingOrder";
    public const string RandomizedRotation = "RandomizedRotation";
    public const string ManualOrder = "ManualOrder";
    public const string SmartSchedule = "SmartSchedule";

    public static bool IsDynamicUnwatched(string? mode)
        => string.Equals(mode, NextUnwatched, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, RandomUnwatched, StringComparison.OrdinalIgnoreCase);

    public static bool TracksJellyfinState(string? mode)
        => IsDynamicUnwatched(mode);

    public static string NormalizeContentMode(string? mode)
    {
        if (string.Equals(mode, NextUnwatched, StringComparison.OrdinalIgnoreCase))
            return NextUnwatched;
        if (string.Equals(mode, RandomUnwatched, StringComparison.OrdinalIgnoreCase))
            return RandomUnwatched;
        if (string.Equals(mode, Random, StringComparison.OrdinalIgnoreCase))
            return Random;

        return Sequential;
    }

    public static string NormalizeSchedulingMethod(string? method)
    {
        // 1.8 and earlier stored "RepeatingSchedule".
        if (string.Equals(method, RandomizedRotation, StringComparison.OrdinalIgnoreCase))
            return RandomizedRotation;
        if (string.Equals(method, ManualOrder, StringComparison.OrdinalIgnoreCase))
            return ManualOrder;
        if (string.Equals(method, SmartSchedule, StringComparison.OrdinalIgnoreCase))
            return SmartSchedule;

        return RepeatingOrder;
    }

    public static int NormalizeBlockMinutes(int minutes)
        => minutes is 20 or 30 or 40 or 45 or 60 or 75 or 90 or 120 ? minutes : 30;

    public static int NormalizeEpisodesPerTurn(int count)
        => count == 2 ? 2 : 1;

    public static int NormalizeSmartRotationMonths(int months)
        => months is 1 or 2 or 3 or 6 ? months : 2;
}
