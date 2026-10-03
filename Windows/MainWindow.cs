using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using BryersHideoutPlugin.Models;
using BryersHideoutPlugin.Services;

namespace BryersHideoutPlugin.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string selectedPlayerId = "";
    private string playerSearch = "";
    private string newPlayerName = "";
    private string balanceText = "0";
    private string balanceReason = "Trade Deposit";
    private string notice = "";
    private string error = "";
    private bool busy;
    private bool authBusy;
    private string loginEmail = "";
    private string loginPassword = "";
    private string newPassword = "";
    private string confirmPassword = "";
    private string authError = "";
    private string authNotice = "";
    private bool confirmDelete;
    private string tellTemplateDraft;
    private readonly List<string> announcementDrafts = new();
    private readonly List<string> shoutDrafts = new();

    private static readonly Vector4 Gold = new(0.88f, 0.70f, 0.32f, 1f);
    private static readonly Vector4 Green = new(0.42f, 0.82f, 0.55f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.68f, 0.70f, 1f);
    private static readonly Vector4 OfflineGray = new(0.72f, 0.74f, 0.73f, 1f);

    public MainWindow(Plugin plugin)
        : base("Bryer's Hideout Control##BryersHideoutControl")
    {
        this.plugin = plugin;
        loginEmail = plugin.Configuration.LastStaffEmail ?? string.Empty;
        tellTemplateDraft = plugin.Configuration.TellTemplate;
        announcementDrafts.AddRange(plugin.Configuration.AnnouncementTemplates);
        shoutDrafts.AddRange(plugin.Configuration.ShoutTemplates);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!plugin.Api.IsAuthenticated)
        {
            DrawLoginScreen();
            return;
        }
        if (plugin.Api.RequiresPasswordChange)
        {
            DrawFirstPasswordScreen();
            return;
        }

        DrawTopBar();
        ImGui.Separator();
        DrawStatusLine();
        ImGui.Spacing();

        var snapshot = plugin.Api.Snapshot;
        if (snapshot is null)
        {
            DrawNoSnapshot();
            // Manual shouts work without the website API. Keep their editor
            // accessible even when the remote admin snapshot is unavailable.
            ImGui.Spacing();
            if (ImGui.BeginTabBar("##offline-tabs"))
            {
                if (ImGui.BeginTabItem("Shouts"))
                {
                    DrawShouts();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            return;
        }

        // Keep the operational toolbar/status fixed while the selected tab scrolls.
        if (ImGui.BeginChild("##main-content", new Vector2(0, 0), false))
        {
            if (ImGui.BeginTabBar("##main-tabs"))
            {
                if (plugin.Api.Can("dashboard.view") && ImGui.BeginTabItem("Dashboard"))
                {
                    DrawDashboard(snapshot);
                    ImGui.EndTabItem();
                }
                if (plugin.Api.Can("players.view") && ImGui.BeginTabItem("Players"))
                {
                    DrawPlayers(snapshot);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Trades"))
                {
                    DrawTrades();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Announcements"))
                {
                    DrawAnnouncements();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Shouts"))
                {
                    DrawShouts();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Settings"))
                {
                    DrawSettings();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();

        if (plugin.Api.Can("players.create")) DrawAddPlayerPopup(snapshot);
    }

    private void DrawLoginScreen()
    {
        ImGui.Spacing();
        ImGui.TextColored(Gold, "STAFF LOGIN");
        ImGui.TextWrapped("Use the same email and password registered for the Bryer's Hideout Staff Panel.");
        ImGui.Spacing();

        var width = Math.Min(420f * ImGuiHelpers.GlobalScale, Math.Max(240f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("Email", ref loginEmail, 254);
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("Password", ref loginPassword, 200, ImGuiInputTextFlags.Password);

        ImGui.BeginDisabled(authBusy || string.IsNullOrWhiteSpace(loginEmail) || string.IsNullOrEmpty(loginPassword));
        if (ImGui.Button(authBusy ? "Signing in…" : "Sign In")) _ = LoginAsync();
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(authNotice)) ImGui.TextColored(Green, authNotice);
        if (!string.IsNullOrWhiteSpace(authError)) ImGui.TextColored(Red, authError);
        ImGui.Spacing();
    }

    private void DrawFirstPasswordScreen()
    {
        var identity = plugin.Api.Identity;
        ImGui.Spacing();
        ImGui.TextColored(Gold, "CREATE YOUR STAFF PASSWORD");
        ImGui.TextWrapped($"{identity?.DisplayName ?? "Staff"} ({identity?.Email ?? loginEmail}) is using a first-login temporary password. Create a new personal password to continue.");
        ImGui.Spacing();

        var width = Math.Min(420f * ImGuiHelpers.GlobalScale, Math.Max(240f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("New password", ref newPassword, 200, ImGuiInputTextFlags.Password);
        ImGui.SetNextItemWidth(width);
        ImGui.InputText("Confirm password", ref confirmPassword, 200, ImGuiInputTextFlags.Password);
        ImGui.TextColored(Muted, "Minimum 10 characters and it must be different from the temporary password.");

        ImGui.BeginDisabled(authBusy);
        if (ImGui.Button(authBusy ? "Saving…" : "Save Password")) _ = CompleteFirstPasswordAsync();
        ImGui.SameLine();
        if (ImGui.Button("Logout##first-password")) _ = LogoutAsync();
        ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(authNotice)) ImGui.TextColored(Green, authNotice);
        if (!string.IsNullOrWhiteSpace(authError)) ImGui.TextColored(Red, authError);
    }

    private void DrawTopBar()
    {
        var snapshot = plugin.Api.Snapshot;
        var active = snapshot?.ActiveProfile;
        var toolbarWidth = ImGui.GetContentRegionAvail().X;

        if (plugin.Api.Can("players.create"))
        {
            if (ImGui.Button("+ Add Player")) ImGui.OpenPopup("Add Player##popup");
            ImGui.SameLine();
            if (ImGui.Button("+ Add Target")) _ = AddCurrentTargetAsync();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Automaticaly register current target as a new user.");
            ImGui.SameLine();
        }
        if (plugin.Api.Can("players.view"))
        {
            if (ImGui.Button("Send Target Code")) _ = SendTargetCodeAsync();
            ImGui.SameLine();
        }
        if (ImGui.Button(plugin.Api.IsRefreshing ? "Refreshing…" : "Refresh")) _ = plugin.Api.RefreshAsync(true);
        ImGui.SameLine();
        if (ImGui.Button("Logout")) _ = LogoutAsync();

        if (snapshot is null || active is null) return;

        ImGui.SameLine();
        ImGui.TextColored(Muted, "Venue:");
        ImGui.SameLine();
        if (plugin.Api.Identity?.IsMaster == true)
        {
            var selectorWidth = Math.Clamp(toolbarWidth * 0.17f, 118f * ImGuiHelpers.GlobalScale, 185f * ImGuiHelpers.GlobalScale);
            ImGui.SetNextItemWidth(selectorWidth);
            if (ImGui.BeginCombo("##venue", active.Name))
            {
                foreach (var profile in snapshot.Profiles)
                {
                    var selected = string.Equals(profile.Id, active.Id, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(profile.Name, selected) && !selected) _ = SelectVenueAsync(profile);
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
        else
        {
            ImGui.TextColored(Gold, active.Name);
        }

        ImGui.SameLine();
        var openLabel = active.IsOpen ? "OPEN" : "CLOSED";
        ImGui.TextColored(active.IsOpen ? Green : Red, openLabel);
        if (plugin.Api.Can("profiles.open_close"))
        {
            ImGui.SameLine();
            if (ImGui.Button(active.IsOpen ? "Close Venue" : "Open Venue"))
                _ = RunCommandAsync("set_venue_open", new { profile_id = active.Id, is_open = !active.IsOpen }, active.IsOpen ? "Venue closed." : "Venue opened.");
        }
    }

    private void DrawStatusLine()
    {
        var connected = plugin.Api.IsConnected && plugin.Realtime.IsConnected;
        ImGui.TextColored(connected ? Green : Red, connected ? "Connected" : "Disconnected");
        ImGui.SameLine();
        if (ImGui.Button("Call Support"))
            Dalamud.Utility.Util.OpenLink("https://discord.com/users/896449611168874507");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Need immediate help? Click here.");

        if (!string.IsNullOrWhiteSpace(notice))
            ImGui.TextColored(Green, notice);
        if (!string.IsNullOrWhiteSpace(error)) ImGui.TextWrapped(error);
    }

    private void DrawNoSnapshot()
    {
        ImGui.TextWrapped("Signed in successfully, but the Staff snapshot is not available yet.");
        ImGui.Spacing();
        ImGui.TextWrapped(plugin.Api.StatusMessage);
        ImGui.Spacing();
        if (ImGui.Button(plugin.Api.IsRefreshing ? "Retrying…" : "Retry")) _ = plugin.Api.RefreshAsync(true);
        ImGui.SameLine();
        if (ImGui.Button("Logout##nosnapshot")) _ = LogoutAsync();
    }

    private void DrawDashboard(AdminSnapshot snapshot)
    {
        var active = snapshot.ActiveProfile;
        if (active is null)
        {
            ImGui.Text("No active Venue/Profile.");
            return;
        }

        var players = snapshot.PlayersForProfile(active.Id);
        var boxes = snapshot.LootboxesForProfile(active.Id);
        var boxIds = boxes.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rewards = snapshot.Rewards.Count(x => boxIds.Contains(x.LootboxId));
        var summary = snapshot.DashboardSummary;

        // The authenticated Staff endpoint calculates the same dashboard totals
        // used by the website and exposes them as staff_dashboard_summary. Prefer
        // those server-authoritative values; the legacy calculations below remain
        // as a compatibility fallback for older deployments.
        var dashboardPlayers = summary?.Players ?? players.Count;
        var dashboardActivePlayers = summary?.ActivePlayers ?? players.Count(x => x.IsActive);
        var dashboardLootboxes = summary?.Lootboxes ?? boxes.Count;
        var dashboardRewards = summary?.Rewards ?? rewards;
        var dashboardJackpotRaised = summary?.JackpotRaised ?? snapshot.PeriodJackpotRaisedForProfile(active.Id);
        var dashboardVenueCardTips = summary?.VenueCardPackTips ?? snapshot.VenueCardPackTipsForProfile(active.Id);
        var dashboardVenueProfit = summary?.VenueProfit ?? snapshot.VenueProfitForProfile(active.Id);
        var dashboardNonVenueCardTips = summary?.NonVenueCardPackTips ?? snapshot.CardPackTipsForProfile(active.Id);
        var dashboardNonVenueProfit = summary?.NonVenueProfit ?? snapshot.NonVenueProfitForProfile(active.Id);

        var metrics = new (string Name, string Value, string Note)[]
        {
            ("Players", Format(dashboardPlayers), $"{Format(dashboardActivePlayers)} active"),
            ("Lootboxes", Format(dashboardLootboxes), "Visible in this Venue"),
            ("Rewards", Format(dashboardRewards), "Configured prizes"),
            ("Jackpot Raised", Format(dashboardJackpotRaised), "Current Save All period"),
            ("Venue Card Pack (Tips)", Format(dashboardVenueCardTips), "Sales from Venue card packs"),
            ("Venue Profit", Format(dashboardVenueProfit), "Venue sales + 10% non-venue + minigame share"),
            ("Non-Venue Card Pack (Tips)", Format(dashboardNonVenueCardTips), "Non-Venue Card Pack Gil"),
            ("Non-Venue Profit", Format(dashboardNonVenueProfit), "Non-venue period + minigame share"),
        };

        const int metricColumns = 3;
        if (ImGui.BeginTable("dashboard-metrics", metricColumns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            for (var i = 0; i < metrics.Length; i++)
            {
                ImGui.TableNextColumn();
                ImGui.BeginGroup();
                ImGui.TextColored(Gold, metrics[i].Name);
                ImGui.SetWindowFontScale(1.35f);
                ImGui.Text(metrics[i].Value);
                ImGui.SetWindowFontScale(1f);
                ImGui.TextColored(Muted, metrics[i].Note);
                ImGui.EndGroup();
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Gold, "Active Venue");
        ImGui.Text($"{active.Name}  ·  {(active.IsOpen ? "OPEN" : "CLOSED")}");
        if (!string.IsNullOrWhiteSpace(active.VenueUrl)) ImGui.TextWrapped(active.VenueUrl);
        ImGui.Spacing();
        var target = plugin.TargetAndTell.CurrentPlayerTargetName();
        ImGui.TextColored(Gold, "Current Target");
        ImGui.Text(target ?? "No player character targeted");
    }

    private void DrawPlayers(AdminSnapshot snapshot)
    {
        var active = snapshot.ActiveProfile;
        if (active is null) { ImGui.Text("No active Venue/Profile."); return; }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var searchWidth = Math.Clamp(availableWidth * 0.38f, 190f * ImGuiHelpers.GlobalScale, 280f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##player-search", "Search player…", ref playerSearch, 128);
        ImGui.SameLine();
        var targetName = plugin.TargetAndTell.CurrentPlayerTargetName();
        ImGui.TextColored(Muted, targetName is null ? "Target: none" : $"Target: {targetName}");

        var filteredPlayers = snapshot.PlayersForProfile(active.Id)
            .Where(x => string.IsNullOrWhiteSpace(playerSearch) || x.Name.Contains(playerSearch, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var targetedPlayer = targetName is null
            ? null
            : filteredPlayers.FirstOrDefault(x => TargetAndTellService.NamesMatch(x.Name, targetName));
        var players = filteredPlayers
            .OrderByDescending(x => targetedPlayer is not null && string.Equals(x.Id, targetedPlayer.Id, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.IsOnline)
            .ThenByDescending(x => x.IsOnline ? DateTime.MinValue : x.LastSeenUtc ?? DateTime.MinValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = players.FirstOrDefault(x => x.Id == selectedPlayerId)
                    ?? players.FirstOrDefault(x => TargetAndTellService.NamesMatch(x.Name, targetName ?? ""))
                    ?? players.FirstOrDefault();

        // Preserve the two-pane Players layout at every supported window size.
        // Both panes shrink with the window instead of switching to a vertically
        // stacked layout that changes the whole interface shape.
        var listWidth = Math.Clamp(availableWidth * 0.26f, 150f * ImGuiHelpers.GlobalScale, 285f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginChild("##player-list", new Vector2(listWidth, 0), true))
            DrawPlayerList(snapshot, active, players, targetName);
        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("##player-details", new Vector2(0, 0), false))
        {
            if (selected is null)
            {
                ImGui.Text("No Players in this Venue yet.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(selectedPlayerId)) selectedPlayerId = selected.Id;
                DrawPlayerDetails(snapshot, active, selected, snapshot.MembershipFor(selected.Id, active.Id));
            }
        }
        ImGui.EndChild();
    }

    private void DrawPlayerList(AdminSnapshot snapshot, ProfileModel active, IReadOnlyList<PlayerModel> players, string? targetName)
    {
        foreach (var player in players)
        {
            var membership = snapshot.MembershipFor(player.Id, active.Id);
            var label = $"{player.Name}##{player.Id}";
            var isTargeted = targetName is not null && TargetAndTellService.NamesMatch(player.Name, targetName);
            ImGui.PushStyleColor(ImGuiCol.Text, isTargeted ? Gold : player.IsOnline ? Green : OfflineGray);
            var selected = ImGui.Selectable(label, selectedPlayerId == player.Id);
            ImGui.PopStyleColor();
            if (selected) selectedPlayerId = player.Id;
            if (membership?.IsVip == true)
            {
                ImGui.SameLine();
                ImGui.TextColored(Gold, "VIP");
            }
        }
    }

    private void DrawPlayerDetails(AdminSnapshot snapshot, ProfileModel active, PlayerModel player, MembershipModel? membership)
    {
        ImGui.TextColored(Gold, player.Name);
        ImGui.SameLine();
        ImGui.TextColored(player.IsActive ? Green : Red, player.IsActive ? "ACTIVE" : "DISABLED");
        if (player.IsStaff) { ImGui.SameLine(); ImGui.TextColored(Gold, "TESTER"); }
        if (membership?.IsVip == true) { ImGui.SameLine(); ImGui.TextColored(Gold, "VIP"); }
        ImGui.Text($"Balance: {Format(player.GilBalance)} Gil");
        ImGui.Text($"Access code: {(string.IsNullOrWhiteSpace(membership?.AccessCode) ? "—" : membership.AccessCode)}");
        if (!string.IsNullOrWhiteSpace(membership?.AccessCode))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##code")) ImGui.SetClipboardText(membership!.AccessCode);
            ImGui.SameLine();
            if (ImGui.SmallButton("Existing Code##code")) _ = SendExistingCodeAsync(player, membership, active);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hit this to send a different tell to the target if it's not a New user.");
        }

        ImGui.Spacing();
        var primaryActions = new List<(string Label, Action Click)>();
        if (membership is not null)
            primaryActions.Add(("Send Tell with Code", () => _ = SendTellAsync(player, membership, active)));
        if (plugin.Api.Can("players.codes"))
            primaryActions.Add(("Regenerate Code", () => _ = RegenerateCodeAsync(player, active)));
        if (plugin.Api.Can("players.status"))
            primaryActions.Add((player.IsActive ? "Disable Access" : "Enable Access", () => _ = RunCommandAsync("set_player_active", new { player_id = player.Id, is_active = !player.IsActive }, player.IsActive ? "Player disabled." : "Player enabled.")));
        DrawActionGrid("##player-primary-actions", primaryActions);

        var secondaryActions = new List<(string Label, Action Click)>();
        if (membership is not null && plugin.Api.Can("players.vip"))
            secondaryActions.Add((membership.IsVip ? "Remove VIP" : "Set VIP", () => _ = RunCommandAsync("set_player_vip", new { player_id = player.Id, profile_id = active.Id, is_vip = !membership.IsVip }, membership.IsVip ? "VIP removed." : "VIP enabled.")));
        if (plugin.Api.Can("players.tester"))
            secondaryActions.Add((player.IsStaff ? "Remove Tester" : "Set Tester", () => _ = RunCommandAsync("set_player_staff", new { player_id = player.Id, is_staff = !player.IsStaff }, player.IsStaff ? "Tester flag removed." : "Tester flag enabled.")));
        secondaryActions.Add(("Prize History", () => plugin.PrizeHistoryWindow.OpenFor(player, active)));
        if (plugin.Api.Can("players.profiles") && !string.Equals(player.ActiveProfileId, active.Id, StringComparison.OrdinalIgnoreCase))
            secondaryActions.Add(("Make This Active Venue", () => _ = RunCommandAsync("set_player_active_profile", new { player_id = player.Id, profile_id = active.Id }, "Player active Venue updated.")));
        DrawActionGrid("##player-secondary-actions", secondaryActions);

        if (plugin.Api.Can("players.balance"))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(Gold, "Balance Adjustment");
            if (ImGui.BeginTable("##balance-adjust-grid", 3, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Gil Amount", ImGuiTableColumnFlags.WidthStretch, 0.72f);
                ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch, 1.65f);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 76f * ImGuiHelpers.GlobalScale);

                ImGui.TableNextColumn();
                ImGui.TextColored(Muted, "Gil Amount");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##balance-delta", ref balanceText, 32))
                    balanceText = FormatGilAmountInput(balanceText);

                ImGui.TableNextColumn();
                ImGui.TextColored(Muted, "Reason");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##balance-reason", ref balanceReason, 160);

                ImGui.TableNextColumn();
                ImGui.TextColored(Muted, "Action");
                if (ImGui.Button("Apply")) _ = AdjustBalanceAsync(player);
                ImGui.EndTable();
            }
            ImGui.TextWrapped("Positive values adds Gil, Negative values removes Gil.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Gold, "Venue Access");
        foreach (var item in snapshot.Memberships.Where(x => x.PlayerId == player.Id))
        {
            var profile = snapshot.Profiles.FirstOrDefault(x => x.Id == item.ProfileId);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped($"{profile?.Name ?? item.ProfileId} · {(string.IsNullOrWhiteSpace(item.AccessCode) ? "no code" : item.AccessCode)}{(item.IsVip ? " · VIP" : "")}");
        }

        if (plugin.Api.Can("players.delete"))
        {
            ImGui.Spacing();
            if (!confirmDelete)
            {
                if (ImGui.Button("Delete Player…")) confirmDelete = true;
            }
            else
            {
                ImGui.TextColored(Red, "Delete this Player and revoke all access?");
                if (ImGui.Button("YES, DELETE"))
                {
                    confirmDelete = false;
                    _ = RunCommandAsync("delete_player", new { player_id = player.Id }, "Player deleted.", () => selectedPlayerId = "");
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) confirmDelete = false;
            }
        }
    }

    private static void DrawActionGrid(string id, IReadOnlyList<(string Label, Action Click)> actions)
    {
        if (actions.Count == 0) return;

        var available = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var horizontalPadding = ImGui.GetStyle().FramePadding.X * 2f;
        var used = 0f;

        ImGui.PushID(id);
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var naturalWidth = ImGui.CalcTextSize(action.Label).X + horizontalPadding + 18f * ImGuiHelpers.GlobalScale;
            var buttonWidth = Math.Max(145f * ImGuiHelpers.GlobalScale, naturalWidth);

            if (used > 0f && used + spacing + buttonWidth <= available)
            {
                ImGui.SameLine();
                used += spacing;
            }
            else if (used > 0f)
            {
                used = 0f;
            }

            if (ImGui.Button(action.Label, new Vector2(buttonWidth, 0))) action.Click();
            used += buttonWidth;
        }
        ImGui.PopID();
    }

    private void DrawTrades()
    {
        var enabled = plugin.Configuration.ObserveTrades;
        if (ImGui.Checkbox("Observe Trade Gil", ref enabled))
        {
            plugin.Configuration.ObserveTrades = enabled;
            plugin.Configuration.Save();
        }
        var tradeNarrow = ImGui.GetContentRegionAvail().X < 560f * ImGuiHelpers.GlobalScale;
        if (tradeNarrow) ImGui.NewLine(); else ImGui.SameLine();
        ImGui.TextColored(plugin.TradeMonitor.IsTradeOpen ? Gold : Muted, plugin.TradeMonitor.IsTradeOpen ? "Trade window detected" : "Waiting for a trade");
        if (tradeNarrow) ImGui.NewLine(); else ImGui.SameLine();
        ImGui.TextColored(Muted, $"Current wallet: {Format(plugin.TradeMonitor.ReadGil())} Gil");
        ImGui.TextWrapped("Saves a history of every Gil trade you receive/send.");
        if (ImGui.Button("Clear Trade History")) plugin.TradeMonitor.ClearHistory();
        ImGui.SameLine();
        if (ImGui.Button("Copy CSV")) ImGui.SetClipboardText(BuildTradeCsv(plugin.Configuration.TradeHistory));
        ImGui.Spacing();

        if (ImGui.BeginTable("trade-history", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 0)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 1.15f);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("Direction", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Wallet After", ImGuiTableColumnFlags.WidthStretch, 0.95f);
            ImGui.TableHeadersRow();
            foreach (var entry in plugin.Configuration.TradeHistory)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.Text(entry.PlayerName);
                ImGui.TableNextColumn(); ImGui.TextColored(entry.GilDelta >= 0 ? Green : Gold, entry.Direction);
                ImGui.TableNextColumn(); ImGui.Text((entry.GilDelta >= 0 ? "+" : "-") + Format(entry.Amount));
                ImGui.TableNextColumn(); ImGui.Text(Format(entry.GilAfter));
            }
            ImGui.EndTable();
        }
    }

    private void DrawAnnouncements()
    {
        var enabled = plugin.Configuration.AnnouncementsEnabled;
        if (ImGui.Checkbox("Enable automatic /shout announcements", ref enabled))
        {
            plugin.Configuration.AnnouncementsEnabled = enabled;
            plugin.Configuration.Save();
            plugin.Announcements.ResetLiveCursor();
        }
        ImGui.TextWrapped("Rare/Big wins will automaticaly be /shout in chat using one of the messages you create below. Use {playername} for the player nickname, {prize} for the reward name. Only one shout every 10 minutes will be sent to avoid spam.");
        ImGui.Spacing();

        for (var i = 0; i < announcementDrafts.Count; i++)
        {
            ImGui.PushID(i);
            var draft = announcementDrafts[i];
            ImGui.SetNextItemWidth(Math.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 90f * ImGuiHelpers.GlobalScale));
            if (ImGui.InputText("##message", ref draft, 350)) announcementDrafts[i] = draft;
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                announcementDrafts.RemoveAt(i);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        if (announcementDrafts.Count < 30 && ImGui.Button("+ Add message"))
            announcementDrafts.Add("{playername} uncovered {prize} from {coffer}!");
        ImGui.SameLine();
        if (ImGui.Button("Save Announcements"))
        {
            var drafts = announcementDrafts.Select(x => x.Trim().Replace('\r', ' ').Replace('\n', ' '))
                .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (drafts.Count == 0)
            {
                error = "Add at least one announcement message before saving.";
            }
            else
            {
                plugin.Configuration.AnnouncementTemplates = drafts;
                plugin.Configuration.Save();
                announcementDrafts.Clear();
                announcementDrafts.AddRange(drafts);
                notice = "Announcement messages saved.";
                error = "";
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Muted, plugin.Announcements.Status);
        if (plugin.Configuration.LastAnnouncementShoutUtc != DateTime.MinValue)
        {
            var remaining = TimeSpan.FromMinutes(10) - (DateTime.UtcNow - plugin.Configuration.LastAnnouncementShoutUtc);
            if (remaining > TimeSpan.Zero)
            {
                ImGui.TextColored(Muted, $"Cooldown: {remaining.Minutes:D2}:{remaining.Seconds:D2} remaining");
                ImGui.SameLine();
                if (ImGui.Button("Skip"))
                {
                    plugin.Announcements.SkipCooldown();
                    notice = "Announcement cooldown skipped.";
                    error = "";
                }
            }
            else
                ImGui.TextColored(Green, "Cooldown complete");
        }
    }

    private void DrawShouts()
    {
        ImGui.TextWrapped("Put your shout messages below one by one, the plugin will choose one of them randomly to shout when you type the command /hshout. Use {id:XXXX} to link a item in the message, example: {id:46906}. Hit the button \"Save Shouts\" to save any changes/additions");
        ImGui.Spacing();

        for (var i = 0; i < shoutDrafts.Count; i++)
        {
            ImGui.PushID(i);
            var draft = shoutDrafts[i];
            ImGui.SetNextItemWidth(Math.Max(220f * ImGuiHelpers.GlobalScale,
                ImGui.GetContentRegionAvail().X - 90f * ImGuiHelpers.GlobalScale));
            if (ImGui.InputText("##shout-message", ref draft, 350)) shoutDrafts[i] = draft;
            ImGui.SameLine();
            if (ImGui.Button("Remove##shout"))
            {
                shoutDrafts.RemoveAt(i);
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        if (shoutDrafts.Count < 30 && ImGui.Button("+ Add message##shout"))
            shoutDrafts.Add(string.Empty);
        ImGui.SameLine();
        if (ImGui.Button("Save Shouts"))
        {
            var messages = shoutDrafts.Select(ShoutService.NormalizeTemplate)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // An empty list can be saved intentionally to disable manual shouts.
            plugin.Shouts.SaveMessages(messages);
            shoutDrafts.Clear();
            shoutDrafts.AddRange(messages);
            notice = messages.Count > 0 ? $"Saved {messages.Count} shout message(s)." : "Shout messages cleared.";
            error = "";
        }

    }

    private void DrawSettings()
    {
        ImGui.TextColored(Gold, "Tell Message");
        ImGui.TextWrapped("Use {code} for player's code, {player} for target/player name and {venue} for current venue name. Hit the button \"Save Tell Template\" to save the changes.");
        ImGui.InputTextMultiline("##tell-template", ref tellTemplateDraft, 600, new Vector2(0, 90 * ImGuiHelpers.GlobalScale));
        if (ImGui.Button("Save Tell Template"))
        {
            plugin.Configuration.TellTemplate = tellTemplateDraft.Trim();
            plugin.Configuration.Save();
            notice = "Tell template saved.";
            error = "";
        }

        ImGui.Spacing();
        var openOnLoad = plugin.Configuration.OpenWindowOnLoad;
        if (ImGui.Checkbox("Open plugin window on load", ref openOnLoad))
        {
            plugin.Configuration.OpenWindowOnLoad = openOnLoad;
            plugin.Configuration.Save();
        }
    }

    private void DrawAddPlayerPopup(AdminSnapshot snapshot)
    {
        if (!ImGui.BeginPopupModal("Add Player##popup", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var active = snapshot.ActiveProfile;
        ImGui.Text(active is null ? "No active Venue." : $"Create access in: {active.Name}");
        ImGui.SetNextItemWidth(330 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Character name", ref newPlayerName, 64);
        if (ImGui.Button("Create") && active is not null && newPlayerName.Trim().Length >= 2)
        {
            var name = newPlayerName.Trim();
            newPlayerName = "";
            ImGui.CloseCurrentPopup();
            _ = CreatePlayerAsync(name, active.Id);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private async Task LoginAsync()
    {
        if (authBusy) return;
        authBusy = true;
        authError = "";
        authNotice = "";
        try
        {
            var identity = await plugin.Api.LoginAsync(loginEmail.Trim(), loginPassword).ConfigureAwait(false);
            plugin.Configuration.LastStaffEmail = identity.Email;
            if (identity.IsMaster && !string.IsNullOrWhiteSpace(plugin.Api.SelectedProfileId))
                plugin.Configuration.PreferredVenueId = plugin.Api.SelectedProfileId;
            plugin.Configuration.Save();
            loginEmail = identity.Email;
            loginPassword = "";
            authNotice = identity.MustChangePassword ? "Login accepted. Create your personal password to continue." : $"Welcome, {identity.DisplayName}.";
        }
        catch (Exception ex) { authError = ex.Message; }
        finally { loginPassword = ""; authBusy = false; }
    }

    private async Task CompleteFirstPasswordAsync()
    {
        if (authBusy) return;
        authError = "";
        authNotice = "";
        if (newPassword.Length < 10)
        {
            authError = "Password must contain at least 10 characters.";
            return;
        }
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            authError = "The password confirmation does not match.";
            return;
        }

        authBusy = true;
        try
        {
            await plugin.Api.CompleteFirstPasswordAsync(newPassword).ConfigureAwait(false);
            if (plugin.Api.Identity?.IsMaster == true && !string.IsNullOrWhiteSpace(plugin.Api.SelectedProfileId))
            {
                plugin.Configuration.PreferredVenueId = plugin.Api.SelectedProfileId;
                plugin.Configuration.Save();
            }
            newPassword = "";
            confirmPassword = "";
            authNotice = "Password saved. Staff access unlocked.";
        }
        catch (Exception ex) { authError = ex.Message; }
        finally { authBusy = false; }
    }

    private async Task LogoutAsync()
    {
        if (authBusy) return;
        authBusy = true;
        try
        {
            await plugin.Api.LogoutAsync().ConfigureAwait(false);
            selectedPlayerId = "";
            loginPassword = "";
            newPassword = "";
            confirmPassword = "";
            authError = "";
            authNotice = "";
        }
        finally { authBusy = false; }
    }

    private async Task SelectVenueAsync(ProfileModel profile)
    {
        await GuardedAsync(async () =>
        {
            var changed = await plugin.Api.SelectProfileAsync(profile.Id).ConfigureAwait(false);
            if (!changed) return;
            plugin.Configuration.PreferredVenueId = plugin.Api.SelectedProfileId;
            plugin.Configuration.Save();
            selectedPlayerId = "";
            notice = $"Viewing Venue: {profile.Name}.";
        });
    }

    private async Task CreatePlayerAsync(string name, string profileId)
    {
        await GuardedAsync(async () =>
        {
            var body = await plugin.Api.CommandAsync("create_player", new { player_name = name, profile_id = profileId });
            var code = body?["access_code"]?.GetValue<string>() ?? "";
            notice = string.IsNullOrWhiteSpace(code) ? $"{name} created/linked." : $"{name} ready · code {code}";
            var snapshot = plugin.Api.Snapshot;
            selectedPlayerId = snapshot?.Players.FirstOrDefault(x => TargetAndTellService.NamesMatch(x.Name, name))?.Id ?? selectedPlayerId;
        });
    }

    private async Task AddCurrentTargetAsync()
    {
        var snapshot = plugin.Api.Snapshot;
        var active = snapshot?.ActiveProfile;
        var name = plugin.TargetAndTell.CurrentPlayerTargetName();
        if (active is null) { error = "No active Venue/Profile."; return; }
        if (name is null) { error = "Target a player character first."; return; }
        await CreatePlayerAsync(name, active.Id);
    }

    private async Task SendTargetCodeAsync()
    {
        var snapshot = plugin.Api.Snapshot;
        var active = snapshot?.ActiveProfile;
        var name = plugin.TargetAndTell.CurrentPlayerTargetName();
        if (snapshot is null || active is null) { error = "Site snapshot is not ready."; return; }
        if (name is null) { error = "Target a player character first."; return; }
        var player = snapshot.PlayersForProfile(active.Id).FirstOrDefault(x => TargetAndTellService.NamesMatch(x.Name, name));
        if (player is null) { error = $"{name} has no access in the active Venue."; return; }
        var membership = snapshot.MembershipFor(player.Id, active.Id);
        if (membership is null) { error = "Player membership is missing."; return; }
        await SendTellAsync(player, membership, active);
    }

    private async Task SendTellAsync(PlayerModel player, MembershipModel membership, ProfileModel active)
    {
        await GuardedAsync(() =>
        {
            plugin.TargetAndTell.SendCodeToCurrentTarget(player, membership, active, plugin.Configuration.TellTemplate);
            notice = $"Tell sent to {player.Name}.";
            return Task.CompletedTask;
        });
    }

    private async Task SendExistingCodeAsync(PlayerModel player, MembershipModel membership, ProfileModel active)
    {
        await GuardedAsync(() =>
        {
            plugin.TargetAndTell.SendCodeToCurrentTarget(player, membership, active, "{code} -> https://bit.ly/lootboxs Happy to see you again!");
            notice = $"Existing code sent to {player.Name}.";
            return Task.CompletedTask;
        });
    }

    private async Task RegenerateCodeAsync(PlayerModel player, ProfileModel active)
    {
        await GuardedAsync(async () =>
        {
            var body = await plugin.Api.CommandAsync("regenerate_code", new { player_id = player.Id, profile_id = active.Id });
            var code = body?["access_code"]?.GetValue<string>() ?? "";
            notice = string.IsNullOrWhiteSpace(code) ? "Access code regenerated." : $"New code: {code}";
        });
    }

    private async Task AdjustBalanceAsync(PlayerModel player)
    {
        if (!TryParseGilAmountInput(balanceText, out var delta) || delta < -999_999_999 || delta > 999_999_999)
        {
            error = "Balance delta must be an integer between -999,999,999 and 999,999,999.";
            return;
        }
        await RunCommandAsync("adjust_balance", new { player_id = player.Id, delta, reason = balanceReason.Trim() }, $"Balance adjusted by {FormatSigned(delta)} Gil.");
    }

    private async Task RunCommandAsync(string action, object payload, string success, Action? after = null)
    {
        await GuardedAsync(async () =>
        {
            await plugin.Api.CommandAsync(action, payload);
            notice = success;
            after?.Invoke();
        });
    }

    private async Task GuardedAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        error = "";
        notice = "";
        try { await action(); }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }
    }

    private static string FormatGilAmountInput(string value)
    {
        var raw = (value ?? string.Empty).Trim();
        var negative = raw.StartsWith("-", StringComparison.Ordinal);
        var allDigits = new string(raw.Where(char.IsDigit).ToArray());
        if (allDigits.Length == 0) return negative ? "-" : string.Empty;
        var digits = allDigits.TrimStart('0');
        if (digits.Length == 0) digits = "0";

        var groups = new List<string>();
        for (var end = digits.Length; end > 0; end -= 3)
        {
            var start = Math.Max(0, end - 3);
            groups.Add(digits[start..end]);
        }
        groups.Reverse();
        return (negative ? "-" : string.Empty) + string.Join(".", groups);
    }

    private static bool TryParseGilAmountInput(string value, out long amount)
    {
        amount = 0;
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0 || raw == "-") return false;
        var negative = raw.StartsWith("-", StringComparison.Ordinal);
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var absolute))
            return false;
        amount = negative ? -absolute : absolute;
        return true;
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string FormatSigned(long value) => (value >= 0 ? "+" : "-") + Format(Math.Abs(value));

    private static string BuildTradeCsv(IEnumerable<TradeHistoryEntry> entries)
    {
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var lines = new List<string> { "timestamp,character,direction,gil_delta,gil_before,gil_after" };
        lines.AddRange(entries.Select(x => string.Join(',',
            Csv(x.TimestampUtc.ToString("O")), Csv(x.PlayerName), x.Direction,
            x.GilDelta.ToString(CultureInfo.InvariantCulture), x.GilBefore.ToString(CultureInfo.InvariantCulture), x.GilAfter.ToString(CultureInfo.InvariantCulture))));
        return string.Join(Environment.NewLine, lines);
    }
}
