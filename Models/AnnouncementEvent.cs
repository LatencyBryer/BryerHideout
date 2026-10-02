using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace BryersHideoutPlugin.Models;

/// <summary>One original Fortune Echo reward announcement (site_events.event_type = rare_win).</summary>
public sealed record AnnouncementEvent(
    string Id, DateTime CreatedAtUtc, string PlayerName, string Prize, string Coffer, string Amount, string RewardId, uint? IngameId)
{
    public static IReadOnlyList<AnnouncementEvent> ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return Array.Empty<AnnouncementEvent>();

        var results = new List<AnnouncementEvent>();
        foreach (var row in events.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || Read(row, "event_type") != "rare_win") continue;
            var id = Read(row, "id");
            if (id.Length == 0 || !DateTime.TryParse(Read(row, "created_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)) continue;

            var payload = row.TryGetProperty("payload", out var raw) ? raw : default;
            JsonDocument? ownedPayload = null;
            try
            {
                if (payload.ValueKind == JsonValueKind.String)
                {
                    ownedPayload = JsonDocument.Parse(payload.GetString() ?? "{}");
                    payload = ownedPayload.RootElement;
                }
                var player = Read(row, "player_name").Trim();
                var prize = Read(payload, "reward_name").Trim();
                var coffer = Read(payload, "lootbox_name").Trim();
                var rewardId = Read(payload, "reward_id").Trim();
                var amountText = Read(payload, "amount");
                var amount = long.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? Math.Max(0, n).ToString("N0", CultureInfo.InvariantCulture) : "0";
                if (Read(payload, "reward_type") == "gil" && long.TryParse(amountText, out var gil) && gil > 0)
                    prize = amount + " Gil";
                uint? ingameId = null;
                if (Read(payload, "reward_type") != "gil" &&
                    uint.TryParse(Read(payload, "ingame_id"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) &&
                    parsedId > 0)
                    ingameId = parsedId;
                if (player.Length > 0 && prize.Length > 0)
                    results.Add(new AnnouncementEvent(id, created, player, prize, coffer, amount, rewardId, ingameId));
            }
            catch (JsonException) { /* A malformed event is ignored, never echoed to game chat. */ }
            finally { ownedPayload?.Dispose(); }
        }
        return results;
    }

    private static string Read(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" :
            value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : "";
    }
}
