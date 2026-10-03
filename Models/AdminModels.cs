using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BryersHideoutPlugin.Models;

public sealed record ProfileModel(string Id, string Name, bool IsActive, bool IsOpen, string VenueUrl);
public sealed record PlayerModel(
    string Id,
    string Name,
    long GilBalance,
    bool IsActive,
    bool IsVip,
    bool IsStaff,
    string ActiveProfileId,
    bool IsOnline,
    DateTime? LastSeenUtc);
public sealed record MembershipModel(string PlayerId, string ProfileId, string AccessCode, string AccessCodeHint, bool IsVip);
public sealed record LootboxModel(string Id, string Name, string ProfileId, bool IsGlobal, bool IsActive, bool IsCardPack);
public sealed record RewardModel(string Id, string LootboxId, string Name, uint? IngameId);
public sealed record CardModel(string Id, string LootboxId, string Name, bool IsActive);
public sealed record LootboxStatModel(string LootboxId, long TotalGilSpent);
public sealed record VenueProfitModel(string ProfileId, long ProfitGil);
public sealed record ProfileSalesFinancialModel(string ProfileId, long CardPackTips, long VenueOnlySales, long VenueOnlyCardPackSales);
public sealed record ProfileFinancialOffsetModel(string ProfileId, long CashflowProfitOffset, long CardPackTipsOffset, long VenueOnlySalesOffset, long VenueOnlyCardPackSalesOffset, long JackpotRaisedOffset);
public sealed record ProfileJackpotTotalModel(string ProfileId, long SlotsRaised, long BlackPrismRaised);
public sealed record ProfileMinigameProfitPeriodModel(string ProfileId, long MinigameLoss, long NonVenueProfitContribution, long VenueProfitContribution);
public sealed record StaffDashboardSummaryModel(
    long Players,
    long ActivePlayers,
    long Lootboxes,
    long Rewards,
    long Cards,
    long JackpotRaised,
    long VenueCardPackTips,
    long NonVenueCardPackTips,
    long VenueProfit,
    long NonVenueProfit);

public sealed record PrizeWinModel(
    string Id,
    string PlayerId,
    string LootboxId,
    string RewardId,
    string RewardName,
    string RewardIcon,
    long Amount,
    string Rarity,
    string RewardType,
    string ProfileId,
    string ProfileName,
    string LootboxName,
    bool IsClaimed,
    bool IsRefunded,
    DateTime? RefundedAtUtc,
    string RefundedBy,
    bool IsUpgradeReward,
    DateTime CreatedAtUtc)
{
    public static IReadOnlyList<PrizeWinModel> ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("wins", out var wins) || wins.ValueKind != JsonValueKind.Array)
            return Array.Empty<PrizeWinModel>();

        var list = new List<PrizeWinModel>();
        foreach (var x in wins.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(x, "id");
            if (id.Length == 0) continue;
            var created = DateTime.TryParse(ReadString(x, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : DateTime.MinValue;
            list.Add(new PrizeWinModel(
                id,
                ReadString(x, "player_id"),
                ReadString(x, "lootbox_id"),
                ReadString(x, "reward_id"),
                ReadString(x, "reward_name_snapshot", "Prize"),
                ReadString(x, "reward_icon_snapshot", "/crystal.svg"),
                ReadLong(x, "amount_snapshot"),
                ReadString(x, "rarity_snapshot", "common"),
                ReadString(x, "reward_type_snapshot"),
                ReadString(x, "profile_id"),
                ReadString(x, "profile_name_snapshot"),
                ReadString(x, "lootbox_name_snapshot", "Coffer"),
                ReadBool(x, "is_claimed"),
                ReadBool(x, "is_refunded"),
                ReadNullableDateUtc(x, "refunded_at"),
                ReadString(x, "refunded_by"),
                ReadBool(x, "is_upgrade_reward"),
                created));
        }
        return list;
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    private static DateTime? ReadNullableDateUtc(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static long ReadLong(JsonElement element, string name, long fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static bool ReadBool(JsonElement element, string name, bool fallback = false)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n != 0 : fallback,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : value.GetString() == "1",
            _ => fallback,
        };
    }
}

public sealed class AdminSnapshot
{
    public string ApiVersion { get; init; } = "";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.MinValue;
    public ProfileModel? ActiveProfile { get; init; }
    public IReadOnlyList<ProfileModel> Profiles { get; init; } = Array.Empty<ProfileModel>();
    public IReadOnlyList<PlayerModel> Players { get; set; } = Array.Empty<PlayerModel>();
    public IReadOnlyList<MembershipModel> Memberships { get; init; } = Array.Empty<MembershipModel>();
    public IReadOnlyList<LootboxModel> Lootboxes { get; init; } = Array.Empty<LootboxModel>();
    public IReadOnlyList<RewardModel> Rewards { get; init; } = Array.Empty<RewardModel>();
    public IReadOnlyList<CardModel> Cards { get; init; } = Array.Empty<CardModel>();
    public IReadOnlyList<LootboxStatModel> LootboxStats { get; init; } = Array.Empty<LootboxStatModel>();
    public IReadOnlyList<VenueProfitModel> VenueProfits { get; init; } = Array.Empty<VenueProfitModel>();
    public IReadOnlyList<ProfileSalesFinancialModel> ProfileSalesFinancials { get; init; } = Array.Empty<ProfileSalesFinancialModel>();
    public IReadOnlyList<ProfileFinancialOffsetModel> ProfileFinancialOffsets { get; init; } = Array.Empty<ProfileFinancialOffsetModel>();
    public IReadOnlyList<ProfileJackpotTotalModel> ProfileJackpotTotals { get; set; } = Array.Empty<ProfileJackpotTotalModel>();
    public IReadOnlyList<ProfileMinigameProfitPeriodModel> ProfileMinigameProfitPeriods { get; set; } = Array.Empty<ProfileMinigameProfitPeriodModel>();
    public long SlotsJackpot { get; set; } = 5_000_000;
    public long BlackPrismJackpot { get; set; } = 5_000_000;
    public StaffDashboardSummaryModel? DashboardSummary { get; init; }

    public IReadOnlyList<MembershipModel> MembershipsForProfile(string profileId) =>
        Memberships.Where(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)).ToArray();

    public IReadOnlyList<PlayerModel> PlayersForProfile(string profileId)
    {
        var ids = MembershipsForProfile(profileId).Select(x => x.PlayerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count > 0) return Players.Where(x => ids.Contains(x.Id)).ToArray();

        // Compatibility fallback for Staff snapshots that only expose the active
        // Venue directly on the Player row. This keeps the list usable even if a
        // deployment omits the legacy top-level player_profiles projection.
        return Players.Where(x => string.Equals(x.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public MembershipModel? MembershipFor(string playerId, string profileId) =>
        Memberships.FirstOrDefault(x => string.Equals(x.PlayerId, playerId, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LootboxModel> LootboxesForProfile(string profileId) =>
        Lootboxes.Where(x => x.IsGlobal || string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)).ToArray();

    public long ProfitForProfile(string profileId) =>
        VenueProfits.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))?.ProfitGil ?? 0;

    public long JackpotRaised =>
        Math.Max(0L, SlotsJackpot - 5_000_000L) + Math.Max(0L, BlackPrismJackpot - 5_000_000L);

    public long JackpotRaisedForProfile(string profileId)
    {
        var row = ProfileJackpotTotals.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        return row is null ? 0L : Math.Max(0L, row.SlotsRaised) + Math.Max(0L, row.BlackPrismRaised);
    }

    private ProfileSalesFinancialModel? SalesFinancialForProfile(string profileId) =>
        ProfileSalesFinancials.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    private ProfileFinancialOffsetModel? FinancialOffsetForProfile(string profileId) =>
        ProfileFinancialOffsets.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    public long PeriodJackpotRaisedForProfile(string profileId)
    {
        var raw = JackpotRaisedForProfile(profileId);
        var offset = Math.Min(raw, Math.Max(0L, FinancialOffsetForProfile(profileId)?.JackpotRaisedOffset ?? 0L));
        return Math.Max(0L, raw - offset);
    }

    public long VenueCardPackTipsForProfile(string profileId)
    {
        var sales = SalesFinancialForProfile(profileId);
        var offset = FinancialOffsetForProfile(profileId);
        return Math.Max(0L, (sales?.VenueOnlyCardPackSales ?? 0L) - (offset?.VenueOnlyCardPackSalesOffset ?? 0L));
    }

    public long CardPackTipsForProfile(string profileId)
    {
        var sales = SalesFinancialForProfile(profileId);
        var offset = FinancialOffsetForProfile(profileId);
        var allCards = Math.Max(0L, (sales?.CardPackTips ?? 0L) - (offset?.CardPackTipsOffset ?? 0L));
        return Math.Max(0L, allCards - VenueCardPackTipsForProfile(profileId));
    }

    private ProfileMinigameProfitPeriodModel? MinigameProfitPeriodForProfile(string profileId) =>
        ProfileMinigameProfitPeriods.FirstOrDefault(x => string.Equals(x.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

    private long BaseNonVenueProfitForProfile(string profileId)
    {
        var sales = SalesFinancialForProfile(profileId);
        var offset = FinancialOffsetForProfile(profileId);
        var minigame = MinigameProfitPeriodForProfile(profileId);
        var cashflow = ProfitForProfile(profileId) - (offset?.CashflowProfitOffset ?? 0L);
        var venueOnly = Math.Max(0L, (sales?.VenueOnlySales ?? 0L) - (offset?.VenueOnlySalesOffset ?? 0L));
        var classified = CardPackTipsForProfile(profileId) + venueOnly + Math.Max(0L, minigame?.MinigameLoss ?? 0L);
        return cashflow > 0L ? Math.Max(0L, cashflow - classified) : cashflow;
    }

    public long NonVenueProfitForProfile(string profileId)
    {
        var minigame = MinigameProfitPeriodForProfile(profileId);
        return BaseNonVenueProfitForProfile(profileId) + Math.Max(0L, minigame?.NonVenueProfitContribution ?? 0L);
    }

    public long VenueProfitForProfile(string profileId)
    {
        var sales = SalesFinancialForProfile(profileId);
        var offset = FinancialOffsetForProfile(profileId);
        var minigame = MinigameProfitPeriodForProfile(profileId);
        var venueOnly = Math.Max(0L, (sales?.VenueOnlySales ?? 0L) - (offset?.VenueOnlySalesOffset ?? 0L));
        var venueOnlyNonCards = Math.Max(0L, venueOnly - VenueCardPackTipsForProfile(profileId));
        var baseNonVenue = BaseNonVenueProfitForProfile(profileId);
        var share = (long)Math.Floor(baseNonVenue * 0.10d + 0.5d);
        return venueOnlyNonCards + share + Math.Max(0L, minigame?.VenueProfitContribution ?? 0L);
    }

    public long OverallProfitForProfile(string profileId) => NonVenueProfitForProfile(profileId);

    public static AdminSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var profiles = ReadArray(root, "profiles").Select(ParseProfile).Where(x => x is not null).Cast<ProfileModel>().ToArray();
        var playerElements = ReadArray(root, "players").ToArray();

        var membershipMap = new Dictionary<string, MembershipModel>(StringComparer.OrdinalIgnoreCase);
        static string MembershipKey(string playerId, string profileId) => $"{playerId}\n{profileId}";
        void AddMembership(JsonElement membership, string fallbackPlayerId = "")
        {
            var playerId = Str(membership, "player_id", fallbackPlayerId);
            var profileId = Str(membership, "profile_id");
            if (playerId.Length == 0 || profileId.Length == 0) return;
            membershipMap[MembershipKey(playerId, profileId)] = new MembershipModel(
                playerId,
                profileId,
                Str(membership, "current_access_code"),
                Str(membership, "access_code_hint"),
                Bool(membership, "is_vip"));
        }

        foreach (var membership in ReadArray(root, "player_profiles")) AddMembership(membership);
        foreach (var player in playerElements)
        {
            var playerId = Str(player, "id");
            if (!player.TryGetProperty("profile_memberships", out var nested) || nested.ValueKind != JsonValueKind.Array) continue;
            foreach (var membership in nested.EnumerateArray())
                if (membership.ValueKind == JsonValueKind.Object) AddMembership(membership, playerId);
        }
        var memberships = membershipMap.Values.ToArray();

        ProfileModel? active = null;
        if (root.TryGetProperty("active_profile", out var activeElement) && activeElement.ValueKind == JsonValueKind.Object)
            active = ParseProfile(activeElement);
        active ??= profiles.FirstOrDefault(x => x.IsActive) ?? profiles.FirstOrDefault();

        var jackpot = 5_000_000L;
        if (root.TryGetProperty("_slots_global_jackpot", out var jackpotElement) && jackpotElement.ValueKind == JsonValueKind.Object)
            jackpot = Math.Max(5_000_000L, Long(jackpotElement, "amount", 5_000_000));

        var blackPrismJackpot = 5_000_000L;
        if (root.TryGetProperty("_black_prism_global_jackpot", out var blackPrismElement) && blackPrismElement.ValueKind == JsonValueKind.Object)
            blackPrismJackpot = Math.Max(5_000_000L, Long(blackPrismElement, "amount", 5_000_000));

        return new AdminSnapshot
        {
            ApiVersion = Str(root, "api_version"),
            GeneratedAtUtc = DateTime.TryParse(Str(root, "generated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var generated) ? generated : DateTime.UtcNow,
            ActiveProfile = active,
            Profiles = profiles,
            Players = playerElements.Select(x => new PlayerModel(
                Str(x, "id"),
                Str(x, "player_name"),
                Long(x, "gil_balance"),
                Bool(x, "is_active", true),
                Bool(x, "is_vip"),
                Bool(x, "is_staff"),
                Str(x, "active_profile_id"),
                false,
                DateUtc(x, "last_login_at"))).Where(x => x.Id.Length > 0).ToArray(),
            Memberships = memberships,
            Lootboxes = ReadArray(root, "lootboxes").Select(x => new LootboxModel(
                Str(x, "id"), Str(x, "name"), Str(x, "profile_id"), Bool(x, "is_global"), Bool(x, "is_active", true), Bool(x, "is_card_pack"))).Where(x => x.Id.Length > 0).ToArray(),
            Rewards = ReadArray(root, "rewards").Select(x =>
            {
                var rawIngameId = Long(x, "ingame_id");
                var ingameId = rawIngameId > 0 && rawIngameId <= uint.MaxValue ? (uint?)rawIngameId : null;
                return new RewardModel(Str(x, "id"), Str(x, "lootbox_id"), Str(x, "name"), ingameId);
            }).Where(x => x.Id.Length > 0).ToArray(),
            Cards = ReadArray(root, "cards").Select(x => new CardModel(Str(x, "id"), Str(x, "lootbox_id"), Str(x, "name"), Bool(x, "is_card_enabled", Bool(x, "is_active", true)))).Where(x => x.Id.Length > 0).ToArray(),
            LootboxStats = ReadArray(root, "lootbox_stats").Select(x => new LootboxStatModel(Str(x, "lootbox_id"), Long(x, "total_gil_spent"))).Where(x => x.LootboxId.Length > 0).ToArray(),
            VenueProfits = ReadArray(root, "_venue_profit_totals").Select(x => new VenueProfitModel(Str(x, "profile_id"), Long(x, "profit_gil"))).Where(x => x.ProfileId.Length > 0).ToArray(),
            ProfileSalesFinancials = ReadArray(root, "_profile_sales_financials").Select(x => new ProfileSalesFinancialModel(Str(x, "profile_id"), Long(x, "card_pack_tips"), Long(x, "venue_only_sales"), Long(x, "venue_only_card_pack_sales"))).Where(x => x.ProfileId.Length > 0).ToArray(),
            ProfileFinancialOffsets = ReadArray(root, "_profile_financial_offsets").Select(x => new ProfileFinancialOffsetModel(Str(x, "profile_id"), Long(x, "cashflow_profit_offset"), Long(x, "card_pack_tips_offset"), Long(x, "venue_only_sales_offset"), Long(x, "venue_only_card_pack_sales_offset"), Long(x, "jackpot_raised_offset"))).Where(x => x.ProfileId.Length > 0).ToArray(),
            ProfileJackpotTotals = ReadArray(root, "_profile_jackpot_totals").Select(x => new ProfileJackpotTotalModel(Str(x, "profile_id"), Long(x, "slots_raised"), Long(x, "black_prism_raised"))).Where(x => x.ProfileId.Length > 0).ToArray(),
            ProfileMinigameProfitPeriods = ReadArray(root, "_profile_minigame_profit_periods").Select(x => new ProfileMinigameProfitPeriodModel(Str(x, "profile_id"), Long(x, "minigame_loss"), Long(x, "non_venue_profit_contribution"), Long(x, "venue_profit_contribution"))).Where(x => x.ProfileId.Length > 0).ToArray(),
            SlotsJackpot = jackpot,
            BlackPrismJackpot = blackPrismJackpot,
            DashboardSummary = ParseDashboardSummary(root),
        };
    }


    private static StaffDashboardSummaryModel? ParseDashboardSummary(JsonElement root)
    {
        if (!root.TryGetProperty("staff_dashboard_summary", out var summary) || summary.ValueKind != JsonValueKind.Object)
            return null;

        return new StaffDashboardSummaryModel(
            Long(summary, "players"),
            Long(summary, "active_players"),
            Long(summary, "lootboxes"),
            Long(summary, "rewards"),
            Long(summary, "cards"),
            Long(summary, "jackpot_raised"),
            Long(summary, "venue_card_pack_tips"),
            Long(summary, "non_venue_card_pack_tips", Long(summary, "card_pack_tips")),
            Long(summary, "venue_profit"),
            Long(summary, "non_venue_profit", Long(summary, "overall_profit")));
    }

    private static ProfileModel? ParseProfile(JsonElement x)
    {
        var id = Str(x, "id");
        if (id.Length == 0) return null;
        return new ProfileModel(id, Str(x, "name", "Unnamed Venue"), Bool(x, "is_active"), Bool(x, "is_open"), Str(x, "venue_url"));
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    private static string Str(JsonElement element, string name, string fallback = "")
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }


    private static DateTime? DateUtc(JsonElement element, string name)
    {
        var raw = Str(element, name);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static bool Bool(JsonElement element, string name, bool fallback = false)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n != 0 : fallback,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : value.GetString() == "1",
            _ => fallback,
        };
    }

    private static long Long(JsonElement element, string name, long fallback = 0)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }
}
