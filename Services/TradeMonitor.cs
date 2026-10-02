using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace BryersHideoutPlugin.Services;

public sealed class TradeMonitor : IDisposable
{
    private readonly Configuration configuration;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameInventory inventory;
    private readonly ITargetManager targets;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource disposeCts = new();
    private bool tradeOpen;
    private long gilBefore;
    private string tradePartner = "Unknown";

    public bool IsTradeOpen => tradeOpen;
    public event Action? HistoryChanged;

    public TradeMonitor(Configuration configuration, IAddonLifecycle addonLifecycle, IGameInventory inventory, ITargetManager targets, IFramework framework, IPluginLog log)
    {
        this.configuration = configuration;
        this.addonLifecycle = addonLifecycle;
        this.inventory = inventory;
        this.targets = targets;
        this.framework = framework;
        this.log = log;
        addonLifecycle.RegisterListener(AddonEvent.PostOpen, "Trade", OnTradeOpened);
        addonLifecycle.RegisterListener(AddonEvent.PreClose, "Trade", OnTradeClosing);
    }

    private void OnTradeOpened(AddonEvent type, AddonArgs args)
    {
        if (!configuration.ObserveTrades) return;
        tradeOpen = true;
        gilBefore = ReadGil();
        tradePartner = CurrentTargetName() ?? "Unknown";
    }

    private void OnTradeClosing(AddonEvent type, AddonArgs args)
    {
        if (!tradeOpen) return;
        tradeOpen = false;
        var before = gilBefore;
        var partner = tradePartner == "Unknown" ? CurrentTargetName() ?? tradePartner : tradePartner;
        _ = FinalizeAfterDelayAsync(before, partner, disposeCts.Token);
    }

    private async Task FinalizeAfterDelayAsync(long before, string partner, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            await framework.Run(() =>
            {
                if (!configuration.ObserveTrades) return;
                var after = ReadGil();
                var delta = after - before;
                if (delta == 0) return; // Cancelled trade or item-only trade.
                configuration.TradeHistory.Insert(0, new TradeHistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    PlayerName = partner,
                    GilDelta = delta,
                    GilBefore = before,
                    GilAfter = after,
                });
                if (configuration.TradeHistory.Count > 500)
                    configuration.TradeHistory.RemoveRange(500, configuration.TradeHistory.Count - 500);
                configuration.Save();
                HistoryChanged?.Invoke();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.Warning(ex, "Failed to finalize observed trade"); }
    }


    private string? CurrentTargetName()
    {
        var target = targets.Target;
        if (target is null || target.ObjectKind != ObjectKind.Pc) return null;
        var name = target.Name.TextValue.Trim();
        return name.Length == 0 ? null : name;
    }

    public long ReadGil()
    {
        try
        {
            var items = inventory.GetInventoryItems(GameInventoryType.Currency);
            // FFXIV item row 1 is Gil. Dalamud exposes the Currency container without
            // needing any native memory hooks.
            foreach (ref readonly var item in items)
                if (!item.IsEmpty && item.BaseItemId == 1) return Math.Max(0, item.Quantity);
        }
        catch (Exception ex) { log.Debug(ex, "Could not read Gil from Currency inventory"); }
        return 0;
    }

    public void ClearHistory()
    {
        configuration.TradeHistory.Clear();
        configuration.Save();
        HistoryChanged?.Invoke();
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        addonLifecycle.UnregisterListener(OnTradeOpened, OnTradeClosing);
        disposeCts.Dispose();
    }
}
