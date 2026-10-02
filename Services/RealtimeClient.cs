using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BryersHideoutPlugin.Models;

namespace BryersHideoutPlugin.Services;

public sealed record JackpotProfileDelta(string ProfileId, long Delta);
public sealed record JackpotRealtimeUpdate(long Amount, bool ProfilesReset, IReadOnlyList<JackpotProfileDelta> ProfileDeltas);
public sealed record PlayerPresenceEntry(string PlayerId, bool IsOnline, DateTime? LastSeenUtc);
public sealed record PlayerPresenceRealtimeUpdate(bool IsSnapshot, IReadOnlyList<PlayerPresenceEntry> Players);

public sealed class RealtimeClient : IAsyncDisposable
{
    public const string RealtimeUrl = "wss://bryers-coffer-backend.kkevinbhrain.workers.dev/v1/realtime";
    private readonly CancellationTokenSource disposeCts = new();
    private readonly object stateSync = new();
    private Task? loopTask;
    private ClientWebSocket? activeSocket;
    private volatile bool enabled;
    public bool IsConnected { get; private set; }
    public string Status { get; private set; } = "Offline";
    public event Action? AdminChanged;
    public event Action? PrizeStateChanged;
    public event Action<AnnouncementEvent>? FortuneEchoAnnouncement;
    public event Action<JackpotRealtimeUpdate>? SlotsJackpotChanged;
    public event Action<JackpotRealtimeUpdate>? BlackPrismJackpotChanged;
    public event Action<PlayerPresenceRealtimeUpdate>? PlayerPresenceChanged;

    public void Start()
    {
        enabled = true;
        if (loopTask is null) loopTask = Task.Run(() => ConnectionLoopAsync(disposeCts.Token));
    }

    public void Stop()
    {
        enabled = false;
        ClientWebSocket? socket;
        lock (stateSync) socket = activeSocket;
        try { socket?.Abort(); } catch { }
        IsConnected = false;
        Status = "Offline";
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var delaySeconds = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!enabled)
            {
                IsConnected = false;
                Status = "Offline";
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                using var socket = new ClientWebSocket();
                lock (stateSync) activeSocket = socket;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                Status = "Connecting…";
                await socket.ConnectAsync(new Uri(RealtimeUrl), cancellationToken).ConfigureAwait(false);
                IsConnected = true;
                Status = "Realtime connected";
                delaySeconds = 1;
                var subscribe = Encoding.UTF8.GetBytes("{\"type\":\"subscribe\",\"topics\":[\"global\",\"plugin-dashboard\",\"slots-jackpot\",\"black-prism-jackpot\",\"fortune-echo-announcement\"]}");
                await socket.SendAsync(new ArraySegment<byte>(subscribe), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

                var buffer = new byte[16 * 1024];
                while (enabled && socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var message = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    var text = Encoding.UTF8.GetString(message.ToArray());
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "";
                        if (type == "postgres_changes")
                        {
                            var topic = root.TryGetProperty("topic", out var topicEl) ? topicEl.GetString() : "";
                            var table = root.TryGetProperty("table", out var tableEl) ? tableEl.GetString() : "";
                            if (string.Equals(topic, "plugin-dashboard", StringComparison.Ordinal)
                                && string.Equals(table, "player_presence", StringComparison.Ordinal)
                                && root.TryGetProperty("new", out var presenceRow)
                                && presenceRow.ValueKind == JsonValueKind.Object)
                            {
                                var kind = presenceRow.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "" : "";
                                var entries = new List<PlayerPresenceEntry>();
                                if (string.Equals(kind, "snapshot", StringComparison.Ordinal)
                                    && presenceRow.TryGetProperty("players", out var playersEl)
                                    && playersEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var playerEl in playersEl.EnumerateArray())
                                    {
                                        if (playerEl.ValueKind != JsonValueKind.Object) continue;
                                        var playerId = playerEl.TryGetProperty("player_id", out var idEl) ? idEl.GetString() ?? "" : "";
                                        if (playerId.Length == 0) continue;
                                        var isOnline = playerEl.TryGetProperty("is_online", out var onlineEl) && onlineEl.ValueKind == JsonValueKind.True;
                                        DateTime? lastSeen = null;
                                        if (playerEl.TryGetProperty("last_seen_at", out var lastEl)
                                            && lastEl.ValueKind == JsonValueKind.String
                                            && DateTime.TryParse(lastEl.GetString(), out var parsed))
                                            lastSeen = parsed.ToUniversalTime();
                                        entries.Add(new PlayerPresenceEntry(playerId, isOnline, lastSeen));
                                    }
                                    PlayerPresenceChanged?.Invoke(new PlayerPresenceRealtimeUpdate(true, entries));
                                }
                                else if (string.Equals(kind, "change", StringComparison.Ordinal))
                                {
                                    var playerId = presenceRow.TryGetProperty("player_id", out var idEl) ? idEl.GetString() ?? "" : "";
                                    if (playerId.Length > 0)
                                    {
                                        var isOnline = presenceRow.TryGetProperty("is_online", out var onlineEl) && onlineEl.ValueKind == JsonValueKind.True;
                                        DateTime? lastSeen = null;
                                        if (presenceRow.TryGetProperty("last_seen_at", out var lastEl)
                                            && lastEl.ValueKind == JsonValueKind.String
                                            && DateTime.TryParse(lastEl.GetString(), out var parsed))
                                            lastSeen = parsed.ToUniversalTime();
                                        entries.Add(new PlayerPresenceEntry(playerId, isOnline, lastSeen));
                                        PlayerPresenceChanged?.Invoke(new PlayerPresenceRealtimeUpdate(false, entries));
                                    }
                                }
                            }
                            else if (string.Equals(topic, "fortune-echo-announcement", StringComparison.Ordinal)
                                && root.TryGetProperty("new", out var announcementRow)
                                && announcementRow.ValueKind == JsonValueKind.Object)
                            {
                                // The server sends the existing site_event row only
                                // when the website has displayed the winning result.
                                var envelope = JsonSerializer.Serialize(new { events = new[] { announcementRow } });
                                foreach (var entry in AnnouncementEvent.ParseResponse(envelope))
                                    FortuneEchoAnnouncement?.Invoke(entry);
                            }
                            else if (string.Equals(topic, "plugin-dashboard", StringComparison.Ordinal))
                            {
                                AdminChanged?.Invoke();
                            }
                            else if (string.Equals(topic, "global", StringComparison.Ordinal))
                            {
                                PrizeStateChanged?.Invoke();

                                // Dashboard invalidation also rides on the existing
                                // global revision event. This is deliberately just a
                                // boolean hint: it adds no D1 query and avoids relying
                                // exclusively on a separate websocket topic.
                                if (root.TryGetProperty("new", out var globalRow)
                                    && globalRow.ValueKind == JsonValueKind.Object
                                    && globalRow.TryGetProperty("plugin_dashboard", out var dashboardEl)
                                    && dashboardEl.ValueKind == JsonValueKind.True)
                                {
                                    AdminChanged?.Invoke();
                                }
                            }
                            else if ((string.Equals(topic, "slots-jackpot", StringComparison.Ordinal)
                                      || string.Equals(topic, "black-prism-jackpot", StringComparison.Ordinal))
                                     && root.TryGetProperty("new", out var row)
                                     && row.ValueKind == JsonValueKind.Object
                                     && row.TryGetProperty("amount", out var amountEl)
                                     && amountEl.TryGetInt64(out var amount))
                            {
                                var resetProfiles = false;
                                if (row.TryGetProperty("profiles_reset", out var resetEl))
                                {
                                    resetProfiles = resetEl.ValueKind == JsonValueKind.True
                                        || (resetEl.ValueKind == JsonValueKind.Number && resetEl.TryGetInt64(out var resetNumber) && resetNumber != 0);
                                }

                                var deltas = new List<JackpotProfileDelta>();
                                if (row.TryGetProperty("profile_deltas", out var deltasEl) && deltasEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var deltaEl in deltasEl.EnumerateArray())
                                    {
                                        if (deltaEl.ValueKind != JsonValueKind.Object) continue;
                                        var profileId = deltaEl.TryGetProperty("profile_id", out var profileEl) ? profileEl.GetString() ?? "" : "";
                                        if (profileId.Length == 0 || !deltaEl.TryGetProperty("delta", out var valueEl) || !valueEl.TryGetInt64(out var delta) || delta <= 0) continue;
                                        deltas.Add(new JackpotProfileDelta(profileId, delta));
                                    }
                                }

                                var update = new JackpotRealtimeUpdate(amount, resetProfiles, deltas);
                                if (string.Equals(topic, "slots-jackpot", StringComparison.Ordinal))
                                    SlotsJackpotChanged?.Invoke(update);
                                else
                                    BlackPrismJackpotChanged?.Invoke(update);
                            }
                        }
                        else if (type == "subscribed")
                        {
                            Status = "Realtime connected";
                        }
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Status = $"Realtime reconnecting: {ex.Message}";
            }
            finally
            {
                lock (stateSync) activeSocket = null;
                IsConnected = false;
            }

            if (!enabled) continue;
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delaySeconds = Math.Min(delaySeconds * 2, 30);
        }
        Status = "Offline";
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        disposeCts.Cancel();
        if (loopTask is not null)
        {
            try { await loopTask.ConfigureAwait(false); } catch { }
        }
        disposeCts.Dispose();
    }
}
