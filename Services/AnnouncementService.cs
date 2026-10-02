using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BryersHideoutPlugin.Models;

namespace BryersHideoutPlugin.Services;

/// <summary>
/// Live-only Fortune Echo -> /shout bridge. A website reveal publishes one
/// verified event over the already connected websocket; this service never
/// polls D1/the feed, and intentionally never replays offline events.
/// </summary>
public sealed class AnnouncementService : IDisposable
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private readonly Plugin plugin;
    private readonly object sync = new();
    private bool sending;
    private bool disposed;
    private volatile string status = "Announcements disabled";

    public string Status => status;

    public AnnouncementService(Plugin plugin) => this.plugin = plugin;

    public void ResetLiveCursor()
    {
        lock (sync)
        {
            status = plugin.Configuration.AnnouncementsEnabled
                ? "Waiting for a revealed Global Announcement…"
                : "Announcements disabled";
        }
    }

    /// <summary>
    /// Called once per newly revealed win from the realtime socket. Events
    /// occurring during a cooldown or while the local player is offline are
    /// ignored, rather than queued for a future shout.
    /// </summary>
    public void ReceiveAnnouncement(AnnouncementEvent entry)
    {
        lock (sync)
        {
            if (disposed) return;
            if (!plugin.Configuration.AnnouncementsEnabled)
            {
                status = "Announcements disabled";
                return;
            }
            if (!Plugin.ClientState.IsLoggedIn)
            {
                status = "Game character is offline; live announcement ignored.";
                return;
            }
            if (DateTime.UtcNow - plugin.Configuration.LastAnnouncementShoutUtc < Cooldown)
            {
                status = "10-minute cooldown active (new events ignored)";
                return;
            }
            if (sending) return; // Do not queue the other wins in a burst.
            sending = true;
        }
        _ = SendLiveAnnouncementAsync(entry);
    }

    private async Task SendLiveAnnouncementAsync(AnnouncementEvent entry)
    {
        try
        {
            var templates = plugin.Configuration.AnnouncementTemplates
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (templates.Length == 0)
            {
                status = "Configure and save at least one announcement message.";
                return;
            }

            var template = templates[Random.Shared.Next(templates.Length)];
            var output = Render(template, entry);
            if (string.IsNullOrWhiteSpace(output)) return;

            // The live site_event historically did not carry ingame_id. Resolve it
            // from the already-loaded Admin snapshot by the immutable reward_id when
            // necessary, so {prize} can still become a real FFXIV item link without
            // adding any polling/query per announcement.
            var resolvedIngameId = ResolveIngameId(entry);

            // Keep the original prize placeholder intact until native item payloads are inserted.
            // If the template has no {prize}, preserve the original plain announcement.
            var withPrizePlaceholder = Render(template, entry, "{prize}");
            var useItemLink = resolvedIngameId.HasValue &&
                Regex.IsMatch(template, @"\{prize\}", RegexOptions.IgnoreCase);

            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                lock (sync)
                {
                    if (disposed || !plugin.Configuration.AnnouncementsEnabled || !Plugin.ClientState.IsLoggedIn)
                        return;
                    if (DateTime.UtcNow - plugin.Configuration.LastAnnouncementShoutUtc < Cooldown)
                        return;
                    if (!useItemLink || !plugin.TargetAndTell.SendShoutWithItemLink(withPrizePlaceholder, resolvedIngameId!.Value))
                        plugin.TargetAndTell.SendShout(output);
                    plugin.Configuration.LastAnnouncementShoutUtc = DateTime.UtcNow;
                    plugin.Configuration.Save();
                    status = "Submitted /shout for " + entry.PlayerName + "; ignoring announcements for 10 minutes.";
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = "Could not send /shout: " + ex.Message;
            Plugin.Log.Warning(ex, "Automatic /shout was not sent");
        }
        finally
        {
            lock (sync) sending = false;
        }
    }

    private uint? ResolveIngameId(AnnouncementEvent entry)
    {
        if (entry.IngameId is > 0) return entry.IngameId;
        if (string.IsNullOrWhiteSpace(entry.RewardId)) return null;

        var reward = plugin.Api.Snapshot?.Rewards.FirstOrDefault(value =>
            string.Equals(value.Id, entry.RewardId, StringComparison.OrdinalIgnoreCase));
        if (reward?.IngameId is > 0)
        {
            Plugin.Log.Debug("Resolved FFXIV item id {ItemId} for reward {RewardId} from Admin snapshot.", reward.IngameId.Value, entry.RewardId);
            return reward.IngameId;
        }

        Plugin.Log.Debug("No FFXIV ingame_id is available for announced reward {RewardId}; using plain prize text.", entry.RewardId);
        return null;
    }

    public void SkipCooldown()
    {
        lock (sync)
        {
            if (disposed) return;
            plugin.Configuration.LastAnnouncementShoutUtc = DateTime.MinValue;
            plugin.Configuration.Save();
            status = plugin.Configuration.AnnouncementsEnabled
                ? "Cooldown skipped; ready for the next revealed Global Announcement."
                : "Announcements disabled";
        }
    }

    public static string Render(string template, AnnouncementEvent entry, string? prizeReplacement = null)
    {
        string Clean(string value) => Regex.Replace(value.Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
        return Clean(template)
            .Replace("{playername}", Clean(entry.PlayerName), StringComparison.OrdinalIgnoreCase)
            .Replace("{prize}", prizeReplacement ?? Clean(entry.Prize), StringComparison.OrdinalIgnoreCase)
            .Replace("{coffer}", Clean(entry.Coffer), StringComparison.OrdinalIgnoreCase)
            .Replace("{amount}", Clean(entry.Amount), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (sync) disposed = true;
    }
}
