using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.VirtualTV.Configuration;

public sealed class ChannelConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional channel description shown on the Jellyfin Live TV channel details page.
    /// This is presentation metadata only and does not affect scheduling or playback.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public int Number { get; set; }

    /// <summary>
    /// Playback architecture for the channel. Existing channels default to PersonalizedTV.
    /// StandardTV is a true linear Live TV stream with a fully materialized schedule and no
    /// per-user watched-state behavior.
    /// </summary>
    public string PlaybackExperience { get; set; } = "PersonalizedTV";

    /// <summary>
    /// Fixed output resolution used by Standard TV's continuous H.264/AAC broadcast stream.
    /// Existing Standard TV channels retain the historical 720p default until edited.
    /// Supported values are 480, 720 and 1080.
    /// </summary>
    public int StandardTvResolution { get; set; } = 720;

    public string ChannelType { get; set; } = "Series";

    /// <summary>
    /// Series playback mode. Supported values are Sequential, NextUnwatched,
    /// RandomUnwatched and Random. Movies retain RandomShuffleCycle.
    /// </summary>
    public string ContentMode { get; set; } = "Sequential";

    /// <summary>
    /// Series scheduling strategy. Supported values are RepeatingOrder,
    /// RandomizedRotation, TrueRandom, ManualOrder and SmartSchedule.
    /// </summary>
    public string SchedulingMethod { get; set; } = "RepeatingOrder";

    /// <summary>
    /// For dynamic unwatched modes, every schedule slot on the channel uses the
    /// same fixed duration. Changing this value requires schedule regeneration.
    /// </summary>
    public int BlockMinutes { get; set; } = 30;

    /// <summary>
    /// Number of consecutive turns allocated to a series before rotating.
    /// For concrete Sequential/Random schedules this is episodes per turn;
    /// for fixed-block modes this is blocks per turn.
    /// </summary>
    public int EpisodesPerTurn { get; set; } = 1;

    /// <summary>
    /// Smart Schedule weekly-template lifetime before it is regenerated.
    /// Supported values are 1, 2, 3 and 6 months.
    /// </summary>
    public int SmartRotationMonths { get; set; } = 2;

    /// <summary>
    /// User that created/owns the channel. Watched-dependent channels are personal to this
    /// administrator in the 1.10 pre-release.
    /// </summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// Stable shuffled series order used by Repeating Order. The list is regenerated only when
    /// explicitly required by Generate New Schedule or when Reconcile detects a changed set.
    /// </summary>
    public List<string> RepeatingSeriesOrder { get; set; } = new();

    /// <summary>
    /// Stable seed and creation anchor for the current Smart weekly template.
    /// </summary>
    public int SmartTemplateSeed { get; set; }
    public string SmartTemplateCreatedUtc { get; set; } = string.Empty;

    /// <summary>
    /// Per-series season and Specials eligibility. Missing entries use backward-compatible
    /// defaults: All Seasons and Specials excluded.
    /// </summary>
    public List<SeriesSelectionConfiguration> SeriesSelections { get; set; } = new();

    public bool VisibleToAllUsers { get; set; } = true;
    public List<string> VisibleUserIds { get; set; } = new();

    /// <summary>
    /// Jellyfin library ids selected to feed this channel.
    /// Series channels only accept TV-show libraries and movie channels only accept movie libraries.
    /// </summary>
    public List<string> SelectedLibraryIds { get; set; } = new();

    /// <summary>
    /// Explicitly selected Series ids for Series channels or Movie ids for Movie channels.
    /// The list order is also the Manual Order sequence.
    /// </summary>
    public List<string> SelectedItemIds { get; set; } = new();

    public bool Is24Hours { get; set; }
    public string OnAirStart { get; set; } = "07:00";
    public string OffAirStart { get; set; } = "02:00";
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public bool NeedsReconcile { get; set; } = true;
    public string ScheduleGeneratedUtc { get; set; } = string.Empty;
    public string ScheduleEndUtc { get; set; } = string.Empty;

    /// <summary>
    /// Hash of currently eligible library content (and, for Movie Random Unwatched, watched
    /// state) used by the daily Reconcile task to detect changes without reshuffling stable
    /// schedules unnecessarily.
    /// </summary>
    public string ContentFingerprint { get; set; } = string.Empty;
}
