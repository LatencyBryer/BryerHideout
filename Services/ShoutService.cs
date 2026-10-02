using System;
using System.Collections.Generic;
using System.Linq;

namespace BryersHideoutPlugin.Services;

/// <summary>
/// Explicitly invoked /hshout: draw from a saved deck without replacement.
/// This service does not use the website events or the automatic shout cooldown.
/// </summary>
public sealed class ShoutService
{
    private readonly Plugin plugin;
    private readonly object sync = new();

    public string Status { get; private set; } = "Ready for /hshout.";

    public ShoutService(Plugin plugin) => this.plugin = plugin;

    public static string NormalizeTemplate(string? message) =>
        (message ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');

    public void SaveMessages(List<string> messages)
    {
        lock (sync)
        {
            var normalized = messages.Select(NormalizeTemplate)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (!plugin.Configuration.ShoutTemplates.SequenceEqual(normalized, StringComparer.Ordinal))
            {
                plugin.Configuration.ShoutTemplates = normalized;
                // When the user edits the list, discard the obsolete deck. The
                // last sent text remains to prevent a repeat at the boundary.
                plugin.Configuration.ShoutRemainingTemplates = new List<string>();
            }
            plugin.Configuration.Save();
            Status = normalized.Count > 0
                ? $"{normalized.Count} message(s) saved. Type /hshout to send one."
                : "No manual shout messages saved; add some in Shouts.";
        }
    }

    public bool TrySend()
    {
        lock (sync)
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                Status = "Log into a character before using /hshout.";
                return false;
            }

            var templates = plugin.Configuration.ShoutTemplates;
            if (templates.Count == 0)
            {
                Status = "Add and save at least one message in the Shouts tab.";
                return false;
            }

            // Use the persisted remainder rather than drawing independently on
            // each invocation. This also prevents repeats across plugin reloads.
            var remaining = plugin.Configuration.ShoutRemainingTemplates
                .Where(value => templates.Contains(value, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (remaining.Count == 0)
                remaining = new List<string>(templates);

            var last = plugin.Configuration.LastManualShoutTemplate;
            var candidates = remaining.Where(value =>
                templates.Count == 1 || !string.Equals(value, last, StringComparison.Ordinal)).ToArray();
            // A migrated or manually edited configuration might contain only
            // the previous message in its remaining deck. In that unusual case
            // start a new full deck rather than send two identical messages in
            // a row (as long as two distinct templates are configured).
            if (candidates.Length == 0 && templates.Count > 1)
            {
                remaining = new List<string>(templates);
                candidates = remaining.Where(value =>
                    !string.Equals(value, last, StringComparison.Ordinal)).ToArray();
            }
            var chosen = candidates[Random.Shared.Next(candidates.Length)];

            try
            {
                // Convert {id:...} tokens into actual game item links using
                // the native chat command path; regular text remains unchanged.
                // A failed send must not consume this template from the deck.
                plugin.TargetAndTell.SendShoutWithInlineItemLinks(chosen);
            }
            catch (Exception ex)
            {
                Status = "Could not submit /shout: " + ex.Message;
                Plugin.Log.Warning(ex, "Manual /hshout could not submit /shout");
                return false; // A failed submission must not consume the entry.
            }

            remaining.Remove(chosen);
            plugin.Configuration.ShoutRemainingTemplates = remaining;
            plugin.Configuration.LastManualShoutTemplate = chosen;
            plugin.Configuration.Save();
            Status = $"Submitted /shout. {remaining.Count} of {templates.Count} message(s) left in this rotation.";
            return true;
        }
    }
}
