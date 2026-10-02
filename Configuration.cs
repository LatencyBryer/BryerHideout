using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace BryersHideoutPlugin;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool OpenWindowOnLoad { get; set; } = true;
    public string LastStaffEmail { get; set; } = string.Empty;
    public string PreferredVenueId { get; set; } = string.Empty;
    public string TellTemplate { get; set; } = "Welcome to Bryer's Hideout! Your access code is {code}. Enjoy!";
    public bool ObserveTrades { get; set; } = false;
    // In-game /shout announcements are deliberately opt-in. Only the player
    // running this plugin can send them, via their own logged-in character.
    public bool AnnouncementsEnabled { get; set; } = false;
    public List<string> AnnouncementTemplates { get; set; } = new();
    public DateTime LastAnnouncementShoutUtc { get; set; } = DateTime.MinValue;

    // Manual /hshout messages are separate from automatic Global Announcements.
    // The remaining deck is saved to keep the no-repeat cycle across plugin reloads.
    public List<string> ShoutTemplates { get; set; } = new();
    public List<string> ShoutRemainingTemplates { get; set; } = new();
    public string LastManualShoutTemplate { get; set; } = string.Empty;
    public List<TradeHistoryEntry> TradeHistory { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public sealed class TradeHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string PlayerName { get; set; } = "Unknown";
    public long GilDelta { get; set; }
    public long GilBefore { get; set; }
    public long GilAfter { get; set; }

    public string Direction => GilDelta >= 0 ? "Received" : "Sent";
    public long Amount => Math.Abs(GilDelta);
}
