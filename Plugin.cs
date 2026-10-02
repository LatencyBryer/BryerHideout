using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BryersHideoutPlugin.Models;
using BryersHideoutPlugin.Services;
using BryersHideoutPlugin.Windows;

namespace BryersHideoutPlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private static readonly string[] CommandNames = ["/lootbox", "/hideout"];
    private const string ManualShoutCommand = "/hshout";
    private readonly CancellationTokenSource disposeCts = new();
    private readonly WindowSystem windowSystem = new("BryersHideoutPlugin");
    private readonly MainWindow mainWindow;
    private readonly PrizeHistoryWindow prizeHistoryWindow;
    private readonly Task safetyRefreshTask;
    private readonly object realtimeRefreshSync = new();
    private DateTime latestRealtimeSignalUtc = DateTime.MinValue;
    private bool realtimeRefreshLoopRunning;

    public Configuration Configuration { get; }
    public AdminApiClient Api { get; }
    public RealtimeClient Realtime { get; }
    public TargetAndTellService TargetAndTell { get; }
    public TradeMonitor TradeMonitor { get; }
    public PrizeImageCache PrizeImages { get; }
    public AnnouncementService Announcements { get; }
    public ShoutService Shouts { get; }
    public PrizeHistoryWindow PrizeHistoryWindow => prizeHistoryWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.TradeHistory ??= new();
        Configuration.AnnouncementTemplates ??= new();
        Configuration.ShoutTemplates ??= new();
        Configuration.ShoutRemainingTemplates ??= new();
        Configuration.LastManualShoutTemplate ??= string.Empty;

        // Older config deserialization could append the initializer's default
        // announcement to the saved list on every plugin reload. Keep only the
        // user's persisted messages, normalize exact duplicates, and never add a
        // template automatically here.
        var normalizedAnnouncementTemplates = Configuration.AnnouncementTemplates
            .Select(value => (value ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' '))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!Configuration.AnnouncementTemplates.SequenceEqual(normalizedAnnouncementTemplates, StringComparer.Ordinal))
        {
            Configuration.AnnouncementTemplates = normalizedAnnouncementTemplates;
            Configuration.Save();
        }

        // Preserve the manual shout rotation on reload while dropping old or
        // duplicate messages that may exist in a previous configuration.
        var savedShouts = Configuration.ShoutTemplates
            .Select(ShoutService.NormalizeTemplate)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var remainingShouts = Configuration.ShoutRemainingTemplates
            .Where(value => savedShouts.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var lastManualShout = ShoutService.NormalizeTemplate(Configuration.LastManualShoutTemplate);
        if (!Configuration.ShoutTemplates.SequenceEqual(savedShouts, StringComparer.Ordinal) ||
            !Configuration.ShoutRemainingTemplates.SequenceEqual(remainingShouts, StringComparer.Ordinal) ||
            !string.Equals(Configuration.LastManualShoutTemplate, lastManualShout, StringComparison.Ordinal))
        {
            Configuration.ShoutTemplates = savedShouts;
            Configuration.ShoutRemainingTemplates = remainingShouts;
            Configuration.LastManualShoutTemplate = lastManualShout;
            Configuration.Save();
        }

        Api = new AdminApiClient(Configuration.PreferredVenueId);
        Realtime = new RealtimeClient();
        TargetAndTell = new TargetAndTellService(TargetManager, Log);
        TradeMonitor = new TradeMonitor(Configuration, AddonLifecycle, GameInventory, TargetManager, Framework, Log);
        PrizeImages = new PrizeImageCache(TextureProvider, Log);
        Announcements = new AnnouncementService(this);
        Shouts = new ShoutService(this);

        mainWindow = new MainWindow(this);
        prizeHistoryWindow = new PrizeHistoryWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(prizeHistoryWindow);

        foreach (var commandName in CommandNames)
        {
            CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Bryer's Hideout in-game control panel."
            });
        }

        CommandManager.AddHandler(ManualShoutCommand, new CommandInfo(OnManualShout)
        {
            HelpMessage = "Send a randomly selected saved Shouts message via /shout, without repeats until the full list has been used."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Realtime.AdminChanged += OnRealtimeAdminChanged;
        Realtime.PrizeStateChanged += OnRealtimePrizeStateChanged;
        Realtime.FortuneEchoAnnouncement += OnFortuneEchoAnnouncement;
        Realtime.SlotsJackpotChanged += OnSlotsJackpotChanged;
        Realtime.BlackPrismJackpotChanged += OnBlackPrismJackpotChanged;
        Realtime.PlayerPresenceChanged += OnPlayerPresenceChanged;
        Api.AuthenticationChanged += OnAuthenticationChanged;
        safetyRefreshTask = Task.Run(() => SafetyRefreshLoopAsync(disposeCts.Token));

        if (Configuration.OpenWindowOnLoad) mainWindow.IsOpen = true;
        Log.Information("Bryer's Hideout Control loaded.");
    }

    private void OnCommand(string command, string args) => mainWindow.Toggle();

    private void OnManualShout(string command, string args)
    {
        if (!Api.IsAuthenticated || Api.RequiresPasswordChange)
        {
            ChatGui.PrintError("[Bryer's Hideout] Sign in to the plugin with an authorized Staff account first.");
            return;
        }
        // Manual shouts are intentionally independent of automatic announcements
        // and their 10-minute cooldown. Do not send an additional game command.
        if (!Shouts.TrySend()) ChatGui.PrintError("[Bryer's Hideout] " + Shouts.Status);
    }
    public void ToggleMainUi() => mainWindow.Toggle();

    private void OnAuthenticationChanged()
    {
        if (Api.IsAuthenticated && !Api.RequiresPasswordChange)
        {
            Realtime.Start();
            Announcements.ResetLiveCursor();
            return;
        }

        Realtime.Stop();
        prizeHistoryWindow.IsOpen = false;
    }

    private void OnFortuneEchoAnnouncement(AnnouncementEvent announcement)
    {
        if (!Api.IsAuthenticated || Api.RequiresPasswordChange) return;
        Announcements.ReceiveAnnouncement(announcement);
    }

    private void OnRealtimeAdminChanged()
    {
        if (!Api.IsAuthenticated || Api.RequiresPasswordChange) return;
        lock (realtimeRefreshSync)
        {
            latestRealtimeSignalUtc = DateTime.UtcNow;
            if (realtimeRefreshLoopRunning) return;
            realtimeRefreshLoopRunning = true;
        }
        _ = RealtimeRefreshLoopAsync(disposeCts.Token);
    }

    private void OnRealtimePrizeStateChanged()
    {
        if (!Api.IsAuthenticated || Api.RequiresPasswordChange) return;
        prizeHistoryWindow.NotifyRealtimeChange();
    }

    private void OnSlotsJackpotChanged(JackpotRealtimeUpdate update) => Api.ApplySlotsJackpot(update);
    private void OnBlackPrismJackpotChanged(JackpotRealtimeUpdate update) => Api.ApplyBlackPrismJackpot(update);
    private void OnPlayerPresenceChanged(PlayerPresenceRealtimeUpdate update) => Api.ApplyPlayerPresence(update);

    private async Task RealtimeRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTime signalUtc;
                lock (realtimeRefreshSync) signalUtc = latestRealtimeSignalUtc;

                await Task.Delay(650, cancellationToken).ConfigureAwait(false);

                lock (realtimeRefreshSync)
                {
                    if (latestRealtimeSignalUtc > signalUtc) continue;
                }

                // Dashboard invalidations are intentionally low-noise, but still
                // cap the expensive snapshot to at most once every five seconds.
                // Repeated site/staff changes inside that window collapse into one
                // ETag-aware refresh instead of one D1 snapshot per click.
                if (Api.LastRefreshUtc is not null)
                {
                    var remaining = TimeSpan.FromSeconds(5) - (DateTime.UtcNow - Api.LastRefreshUtc.Value);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }

                if (Api.LastRefreshUtc is null || Api.LastRefreshUtc.Value < signalUtc)
                {
                    var refreshed = await Api.RefreshAsync(false, cancellationToken).ConfigureAwait(false);
                    if (!refreshed && (Api.LastRefreshUtc is null || Api.LastRefreshUtc.Value < signalUtc))
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                prizeHistoryWindow.NotifyRealtimeChange();

                lock (realtimeRefreshSync)
                {
                    if (latestRealtimeSignalUtc > signalUtc) continue;
                    realtimeRefreshLoopRunning = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (realtimeRefreshSync) realtimeRefreshLoopRunning = false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Realtime refresh loop failed");
            lock (realtimeRefreshSync) realtimeRefreshLoopRunning = false;
        }
    }

    private async Task SafetyRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                if (!Api.IsAuthenticated || Api.RequiresPasswordChange) continue;
                var beforeVersion = Api.SnapshotVersion;
                await Api.RefreshAsync(false, cancellationToken).ConfigureAwait(false);
                if (Api.SnapshotVersion != beforeVersion)
                    prizeHistoryWindow.NotifyRealtimeChange();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Debug(ex, "Safety refresh failed"); }
        }
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        Announcements.Dispose();
        Realtime.AdminChanged -= OnRealtimeAdminChanged;
        Realtime.PrizeStateChanged -= OnRealtimePrizeStateChanged;
        Realtime.FortuneEchoAnnouncement -= OnFortuneEchoAnnouncement;
        Realtime.SlotsJackpotChanged -= OnSlotsJackpotChanged;
        Realtime.BlackPrismJackpotChanged -= OnBlackPrismJackpotChanged;
        Realtime.PlayerPresenceChanged -= OnPlayerPresenceChanged;
        Api.AuthenticationChanged -= OnAuthenticationChanged;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        foreach (var commandName in CommandNames) CommandManager.RemoveHandler(commandName);
        CommandManager.RemoveHandler(ManualShoutCommand);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        prizeHistoryWindow.Dispose();
        PrizeImages.Dispose();
        TradeMonitor.Dispose();
        try { Realtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { safetyRefreshTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        Api.Dispose();
        disposeCts.Dispose();
    }
}
