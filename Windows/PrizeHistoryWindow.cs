using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using BryersHideoutPlugin.Models;

namespace BryersHideoutPlugin.Windows;

public sealed class PrizeHistoryWindow : Window, IDisposable
{
    private sealed record PrizeGroup(
        string Key,
        PrizeWinModel Win,
        IReadOnlyList<string> WinIds,
        IReadOnlyList<string> ClaimedWinIds,
        IReadOnlyList<string> PendingWinIds,
        int Quantity,
        int ClaimedCount);

    private readonly Plugin plugin;
    private PlayerModel? player;
    private ProfileModel? openedFromProfile;
    private IReadOnlyList<PrizeWinModel> wins = Array.Empty<PrizeWinModel>();
    private bool loading;
    private bool commandBusy;
    private bool confirmClear;
    private string deliveryPromptKey = "";
    private int deliveryClaimQuantity;
    private string refundPromptKey = "";
    private string notice = "";
    private string error = "";
    private DateTime lastLoadUtc = DateTime.MinValue;

    private static readonly Vector4 Gold = new(0.88f, 0.70f, 0.32f, 1f);
    private static readonly Vector4 Green = new(0.42f, 0.82f, 0.55f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.68f, 0.70f, 1f);

    public PrizeHistoryWindow(Plugin plugin)
        : base("Prize Collection##BryersHideoutPrizeCollection")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void OpenFor(PlayerModel selectedPlayer, ProfileModel profile)
    {
        player = selectedPlayer;
        openedFromProfile = profile;
        notice = "";
        error = "";
        deliveryPromptKey = "";
        deliveryClaimQuantity = 0;
        refundPromptKey = "";
        confirmClear = false;
        IsOpen = true;
        _ = RefreshAsync();
    }

    public void NotifyRealtimeChange()
    {
        if (!IsOpen || player is null || loading || commandBusy) return;
        if ((DateTime.UtcNow - lastLoadUtc).TotalMilliseconds < 750) return;
        _ = RefreshAsync();
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(0.90f);
        if (player is null)
        {
            ImGui.Text("Select a Player from the main window first.");
            return;
        }

        ImGui.TextColored(Gold, player.Name);
        if (openedFromProfile is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"· opened from {openedFromProfile.Name}");
        }
        ImGui.SameLine();
        ImGui.TextColored(Muted, $"· {wins.Count:N0} prize record{(wins.Count == 1 ? "" : "s")}");

        if (ImGui.Button(loading ? "Refreshing…" : "Refresh") && !loading) _ = RefreshAsync();
        if (plugin.Api.Identity?.IsMaster == true && plugin.Api.Can("players.prizes"))
        {
            ImGui.SameLine();
            if (!confirmClear)
            {
                if (ImGui.Button("Clear Prize Records…")) confirmClear = true;
            }
            else
            {
                ImGui.TextColored(Red, "Clear every current prize record for this Player?");
                ImGui.SameLine();
                if (ImGui.Button("YES, CLEAR")) _ = ClearPrizesAsync();
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) confirmClear = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(notice)) ImGui.TextColored(Green, notice);
        if (!string.IsNullOrWhiteSpace(error)) ImGui.TextColored(Red, error);
        ImGui.Separator();
        ImGui.Spacing();

        if (loading && wins.Count == 0)
        {
            ImGui.TextColored(Muted, "Loading Prize Collection…");
            return;
        }

        var groups = BuildGroups(wins);
        if (groups.Count == 0)
        {
            ImGui.TextColored(Muted, "This Player currently has no prizes in Prize Collection.");
            return;
        }

        if (ImGui.BeginChild("##prize-history-scroll", new Vector2(0, 0), false))
        {
            foreach (var group in groups) DrawPrizeGroup(group);
        }
        ImGui.EndChild();
    }

    private void DrawPrizeGroup(PrizeGroup group)
    {
        var win = group.Win;
        var refunded = win.IsRefunded;
        var claimedCount = refunded ? 0 : Math.Max(0, Math.Min(group.Quantity, group.ClaimedCount));
        var fullyClaimed = !refunded && claimedCount == group.Quantity;
        var partiallyClaimed = !refunded && claimedCount > 0 && claimedCount < group.Quantity;
        var canManageDelivery = plugin.Api.Can("players.prizes");
        var canRefund = plugin.Api.Can("players.prizes.refund");
        var deliveryPromptOpen = canManageDelivery && deliveryPromptKey == group.Key && !refunded;
        var refundPromptOpen = canRefund && refundPromptKey == group.Key && !refunded;
        var extraHeight = deliveryPromptOpen
            ? (group.Quantity > 1 ? 92f : 56f)
            : 0f;
        if (refundPromptOpen) extraHeight += 66f;
        var rowHeight = (108f + extraHeight) * ImGuiHelpers.GlobalScale;

        if (!ImGui.BeginChild($"##prize-{group.Key}", new Vector2(0, rowHeight), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.EndChild();
            return;
        }

        var imageSize = 64f * ImGuiHelpers.GlobalScale;
        var tableFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV;
        if (ImGui.BeginTable($"##prize-table-{group.Key}", 3, tableFlags, new Vector2(-1, 0)))
        {
            ImGui.TableSetupColumn("Image", ImGuiTableColumnFlags.WidthFixed, imageSize + 8f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Info", ImGuiTableColumnFlags.WidthStretch, 1.35f);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 0.90f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var imagePos = ImGui.GetCursorScreenPos();
            var texture = plugin.PrizeImages.GetOrRequest(win.RewardIcon);
            if (texture is not null)
                ImGui.Image(texture.Handle, new Vector2(imageSize, imageSize));
            else
            {
                ImGui.BeginChild($"##image-placeholder-{group.Key}", new Vector2(imageSize, imageSize), true);
                ImGui.TextColored(Muted, "Image\nloading…");
                ImGui.EndChild();
            }

            var afterImage = ImGui.GetCursorScreenPos();
            var quantityLabel = $"x{group.Quantity:N0}";
            var quantitySize = ImGui.CalcTextSize(quantityLabel);
            var quantityPos = imagePos + new Vector2(
                imageSize - quantitySize.X - 3f * ImGuiHelpers.GlobalScale,
                imageSize - quantitySize.Y - 2f * ImGuiHelpers.GlobalScale);
            var outline = new Vector4(0f, 0f, 0f, 1f);
            var outlineOffset = 1f * ImGuiHelpers.GlobalScale;
            ImGui.SetCursorScreenPos(quantityPos + new Vector2(-outlineOffset, 0)); ImGui.TextColored(outline, quantityLabel);
            ImGui.SetCursorScreenPos(quantityPos + new Vector2(outlineOffset, 0)); ImGui.TextColored(outline, quantityLabel);
            ImGui.SetCursorScreenPos(quantityPos + new Vector2(0, -outlineOffset)); ImGui.TextColored(outline, quantityLabel);
            ImGui.SetCursorScreenPos(quantityPos + new Vector2(0, outlineOffset)); ImGui.TextColored(outline, quantityLabel);
            ImGui.SetCursorScreenPos(quantityPos); ImGui.TextColored(Gold, quantityLabel);
            ImGui.SetCursorScreenPos(afterImage);

            ImGui.TableNextColumn();
            var title = win.RewardName.Length == 0 ? "Prize" : win.RewardName;
            ImGui.TextColored(RarityColor(win.Rarity), title);
            ImGui.TextColored(Muted, $"From {Fallback(win.LootboxName, "Coffer")}");
            ImGui.Text($"Venue: {ProfileHandle(win.ProfileName)}");
            ImGui.Text($"Won: {DateText(win.CreatedAtUtc)}");
            if (win.IsUpgradeReward)
                ImGui.TextColored(Muted, "Upgrade Reward");
            if (refunded)
            {
                var refundedAt = win.RefundedAtUtc.HasValue ? DateText(win.RefundedAtUtc.Value) : "—";
                ImGui.TextColored(Muted, $"Refunded: {refundedAt}");
            }

            ImGui.TableNextColumn();
            ImGui.Text("Status:");
            ImGui.SameLine();
            if (refunded)
                ImGui.TextColored(Red, "Refunded");
            else if (fullyClaimed)
                ImGui.TextColored(Green, "Delivered");
            else if (partiallyClaimed)
                ImGui.TextColored(Gold, $"{claimedCount}/{group.Quantity} Delivered");
            else
                ImGui.TextColored(Gold, "Pending");

            if (!refunded)
            {
                if (canManageDelivery)
                {
                    var delivered = fullyClaimed;
                    if (!commandBusy && ImGui.Checkbox($"Delivered##{group.Key}", ref delivered))
                    {
                        refundPromptKey = "";
                        if (group.Quantity <= 1 && fullyClaimed)
                        {
                            _ = SetClaimedCountAsync(group, 0);
                        }
                        else
                        {
                            deliveryClaimQuantity = group.Quantity > 1 ? claimedCount : 1;
                            deliveryPromptKey = group.Key;
                        }
                    }
                    else if (commandBusy)
                    {
                        ImGui.BeginDisabled(true);
                        ImGui.Checkbox($"Delivered##{group.Key}", ref delivered);
                        ImGui.EndDisabled();
                    }
                }

                if (partiallyClaimed)
                    ImGui.TextColored(Muted, $"{claimedCount} of {group.Quantity} copies claimed");

                if (canRefund)
                {
                    ImGui.BeginDisabled(commandBusy);
                    if (ImGui.Button($"Refund##refund-{group.Key}"))
                    {
                        deliveryPromptKey = "";
                        refundPromptKey = group.Key;
                    }
                    ImGui.EndDisabled();
                }
            }

            if (deliveryPromptOpen)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.BeginDisabled(commandBusy);
                if (group.Quantity > 1)
                {
                    ImGui.TextColored(Gold, "Set claimed quantity");
                    if (ImGui.Button($"-##claim-minus-{group.Key}"))
                        deliveryClaimQuantity = Math.Max(0, deliveryClaimQuantity - 1);
                    ImGui.SameLine();
                    ImGui.Text($"{deliveryClaimQuantity} / {group.Quantity}");
                    ImGui.SameLine();
                    if (ImGui.Button($"+##claim-plus-{group.Key}"))
                        deliveryClaimQuantity = Math.Min(group.Quantity, deliveryClaimQuantity + 1);
                    if (ImGui.Button($"Save##claim-save-{group.Key}")) _ = SetClaimedCountAsync(group, deliveryClaimQuantity);
                    ImGui.SameLine();
                    if (ImGui.Button($"Cancel##claim-cancel-{group.Key}")) deliveryPromptKey = "";
                }
                else
                {
                    ImGui.TextColored(Gold, "Mark this prize as delivered?");
                    if (ImGui.Button($"Yes##deliver-{group.Key}")) _ = SetClaimedCountAsync(group, 1);
                    ImGui.SameLine();
                    if (ImGui.Button($"No##deliver-{group.Key}")) deliveryPromptKey = "";
                }
                ImGui.EndDisabled();
            }

            if (refundPromptOpen)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(Red, group.Quantity > 1
                    ? "Refund one copy of this grouped reward?"
                    : "Refund this prize?");
                ImGui.TextWrapped("The refunded copy is removed from the active reward group and reward stock is restored when that win originally consumed stock.");
                ImGui.BeginDisabled(commandBusy);
                if (ImGui.Button($"Yes, Refund##refund-confirm-{group.Key}")) _ = RefundPrizeAsync(group);
                ImGui.SameLine();
                if (ImGui.Button($"Cancel##refund-cancel-{group.Key}")) refundPromptKey = "";
                ImGui.EndDisabled();
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
        ImGui.Spacing();
    }

    private async Task RefreshAsync()
    {
        if (player is null || loading) return;
        loading = true;
        error = "";
        try
        {
            wins = await plugin.Api.GetPlayerPrizesAsync(player.Id).ConfigureAwait(false);
            lastLoadUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { error = ex.Message; }
        finally { loading = false; }
    }

    private async Task SetClaimedCountAsync(PrizeGroup group, int requestedCount)
    {
        if (commandBusy || group.Win.IsRefunded || !plugin.Api.Can("players.prizes")) return;

        var total = Math.Max(1, group.Quantity);
        var currentClaimed = group.ClaimedWinIds.Count;
        var targetClaimed = Math.Max(0, Math.Min(total, requestedCount));
        if (targetClaimed == currentClaimed)
        {
            deliveryPromptKey = "";
            return;
        }

        var claiming = targetClaimed > currentClaimed;
        var changedIds = claiming
            ? group.PendingWinIds.Take(targetClaimed - currentClaimed).ToArray()
            : group.ClaimedWinIds.Take(currentClaimed - targetClaimed).ToArray();
        if (changedIds.Length == 0)
        {
            deliveryPromptKey = "";
            return;
        }

        commandBusy = true;
        error = "";
        notice = "";
        try
        {
            await plugin.Api.CommandAsync("set_prize_claimed", new { win_ids = changedIds, claimed = claiming }).ConfigureAwait(false);
            deliveryPromptKey = "";
            notice = claiming ? "Prize delivery updated." : "Prize delivery reverted.";
            if (player is not null)
                wins = await plugin.Api.GetPlayerPrizesAsync(player.Id).ConfigureAwait(false);
            lastLoadUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { error = ex.Message; }
        finally { commandBusy = false; }
    }

    private async Task RefundPrizeAsync(PrizeGroup group)
    {
        if (commandBusy || group.Win.IsRefunded || !plugin.Api.Can("players.prizes.refund")) return;
        var winId = group.PendingWinIds.FirstOrDefault()
            ?? group.ClaimedWinIds.FirstOrDefault()
            ?? group.WinIds.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(winId)) return;

        commandBusy = true;
        error = "";
        notice = "";
        try
        {
            await plugin.Api.CommandAsync("refund_prize", new { win_id = winId, refunded_by = "plugin" }).ConfigureAwait(false);
            refundPromptKey = "";
            deliveryPromptKey = "";
            notice = group.Quantity > 1 ? "One prize copy refunded." : "Prize refunded.";
            if (player is not null)
                wins = await plugin.Api.GetPlayerPrizesAsync(player.Id).ConfigureAwait(false);
            lastLoadUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { error = ex.Message; }
        finally { commandBusy = false; }
    }

    private async Task ClearPrizesAsync()
    {
        if (player is null || commandBusy || plugin.Api.Identity?.IsMaster != true || !plugin.Api.Can("players.prizes")) return;
        commandBusy = true;
        error = "";
        notice = "";
        try
        {
            await plugin.Api.CommandAsync("clear_player_prizes", new { player_id = player.Id }).ConfigureAwait(false);
            wins = Array.Empty<PrizeWinModel>();
            confirmClear = false;
            deliveryPromptKey = "";
            deliveryClaimQuantity = 0;
            refundPromptKey = "";
            notice = "Prize records cleared.";
            lastLoadUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { error = ex.Message; }
        finally { commandBusy = false; }
    }

    private static IReadOnlyList<PrizeGroup> BuildGroups(IReadOnlyList<PrizeWinModel> source)
    {
        return source
            .GroupBy(win => $"{Fallback(win.RewardId, Fallback(win.RewardName, win.Id))}|{win.Amount}|{Fallback(win.ProfileId, win.ProfileName)}|{(win.IsRefunded ? "refunded" : "active")}|{(win.IsUpgradeReward ? "upgrade" : "standard")}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id, StringComparer.Ordinal).ToArray();
                var claimedIds = ordered.Where(x => !x.IsRefunded && x.IsClaimed).Select(x => x.Id).ToArray();
                var pendingIds = ordered.Where(x => !x.IsRefunded && !x.IsClaimed).Select(x => x.Id).ToArray();
                return new PrizeGroup(
                    group.Key,
                    ordered[0],
                    ordered.Select(x => x.Id).ToArray(),
                    claimedIds,
                    pendingIds,
                    ordered.Length,
                    claimedIds.Length);
            })
            .OrderByDescending(group => group.Win.CreatedAtUtc)
            .ThenByDescending(group => group.Win.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ProfileHandle(string value)
    {
        var clean = Fallback(value, "Unknown").Trim().TrimStart('@').Replace(" ", "");
        return $"@{clean}";
    }

    private static string DateText(DateTime value) => value == DateTime.MinValue
        ? "—"
        : value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static Vector4 RarityColor(string rarity) => (rarity ?? "").Trim().ToLowerInvariant() switch
    {
        "legendary" => new Vector4(0.96f, 0.72f, 0.18f, 1f),
        "epic" => new Vector4(0.76f, 0.46f, 0.95f, 1f),
        "rare" => new Vector4(0.35f, 0.64f, 0.96f, 1f),
        "uncommon" => new Vector4(0.38f, 0.82f, 0.48f, 1f),
        _ => new Vector4(0.92f, 0.92f, 0.92f, 1f),
    };
}
