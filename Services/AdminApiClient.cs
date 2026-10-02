using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BryersHideoutPlugin.Models;

namespace BryersHideoutPlugin.Services;

public sealed class AdminApiClient : IDisposable
{
    public const string SiteBaseUrl = "https://bryerlootboxs-site.vercel.app";

    private readonly CookieContainer cookieContainer = new();
    private readonly HttpClient http;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object presenceSync = new();
    private readonly Dictionary<string, PlayerPresenceEntry> presenceByPlayer = new(StringComparer.OrdinalIgnoreCase);
    private string? etag;
    private string preferredProfileId;

    public AdminSnapshot? Snapshot { get; private set; }
    public StaffIdentityModel? Identity { get; private set; }
    public bool IsAuthenticated => Identity is not null;
    public bool RequiresPasswordChange => Identity?.MustChangePassword == true;
    public bool IsRefreshing { get; private set; }
    public bool IsConnected { get; private set; }
    public string StatusMessage { get; private set; } = "Sign in with your Staff account.";
    public string SelectedProfileId { get; private set; } = string.Empty;
    public DateTime? LastRefreshUtc { get; private set; }
    public long SnapshotVersion { get; private set; }
    public event Action? SnapshotChanged;
    public event Action? AuthenticationChanged;

    public AdminApiClient(string preferredProfileId = "")
    {
        this.preferredProfileId = preferredProfileId?.Trim() ?? string.Empty;
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.All,
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BryersHideoutPlugin/1.1");
    }

    public bool Can(string permission) => Identity?.Can(permission) == true;

    public async Task<StaffIdentityModel> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = (email ?? string.Empty).Trim();
        if (normalizedEmail.Length == 0 || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Enter your Staff email and password.");

        StatusMessage = "Signing in…";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SiteBaseUrl}/api/staff/login")
        {
            Content = JsonContent(new { email = normalizedEmail, password })
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ClearLocalAuthentication("Sign in failed.");
            throw new InvalidOperationException(TryReadError(text) ?? $"Staff login failed with HTTP {(int)response.StatusCode}.");
        }

        var identity = await LoadIdentityAsync(cancellationToken).ConfigureAwait(false);
        Identity = identity;
        SelectedProfileId = identity.IsMaster
            ? preferredProfileId
            : identity.RoleProfileId;
        etag = null;
        IsConnected = false;
        StatusMessage = identity.MustChangePassword
            ? "Create your personal Staff password to continue."
            : $"Signed in as {identity.DisplayName}.";
        AuthenticationChanged?.Invoke();

        if (!identity.MustChangePassword)
            await RefreshAsync(true, cancellationToken).ConfigureAwait(false);

        return identity;
    }

    public async Task CompleteFirstPasswordAsync(string newPassword, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) throw new InvalidOperationException("Sign in before changing the Staff password.");
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 10)
            throw new InvalidOperationException("Password must contain at least 10 characters.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SiteBaseUrl}/api/staff/password")
        {
            Content = JsonContent(new { password = newPassword })
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) HandleUnauthorized();
            throw new InvalidOperationException(TryReadError(text) ?? $"Could not update Staff password (HTTP {(int)response.StatusCode}).");
        }

        Identity = await LoadIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (Identity is null || Identity.MustChangePassword)
            throw new InvalidOperationException("The Staff password was saved, but the account is still waiting for first-login setup.");

        SelectedProfileId = Identity.IsMaster ? preferredProfileId : Identity.RoleProfileId;
        etag = null;
        AuthenticationChanged?.Invoke();
        await RefreshAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{SiteBaseUrl}/api/staff/logout");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _ = response.StatusCode;
        }
        catch
        {
            // Local logout still wins. The server session expires/revokes normally.
        }
        ClearLocalAuthentication("Signed out.");
    }

    public async Task<bool> SelectProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) throw new InvalidOperationException("Staff authentication required.");
        if (Identity?.IsMaster != true)
        {
            SelectedProfileId = Identity?.RoleProfileId ?? string.Empty;
            return false;
        }

        var clean = (profileId ?? string.Empty).Trim();
        if (clean.Length == 0) return false;
        preferredProfileId = clean;
        SelectedProfileId = clean;
        etag = null;
        return await RefreshAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated || RequiresPasswordChange)
        {
            IsConnected = false;
            StatusMessage = RequiresPasswordChange
                ? "Create your personal Staff password to continue."
                : "Sign in with your Staff account.";
            return false;
        }

        if (!await requestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        IsRefreshing = true;
        try
        {
            var result = await RequestSnapshotAsync(force, cancellationToken).ConfigureAwait(false);
            return result is SnapshotResult.Updated or SnapshotResult.NotModified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusMessage = ex.Message;
            return false;
        }
        finally
        {
            IsRefreshing = false;
            requestGate.Release();
        }
    }

    public async Task<IReadOnlyList<PrizeWinModel>> GetPlayerPrizesAsync(string playerId, int limit = 5000, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("Player id is required.", nameof(playerId));

        var safeLimit = Math.Max(100, Math.Min(5000, limit));
        var url = $"{SiteBaseUrl}/api/staff/player-prizes?player_id={Uri.EscapeDataString(playerId)}&limit={safeLimit}";
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            HandleUnauthorized();
            throw new InvalidOperationException("Your Staff session expired. Sign in again.");
        }
        if (response.StatusCode == (HttpStatusCode)428)
        {
            await ReloadIdentityAfterPasswordRequirementAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Create your personal Staff password before using Prize Collection.");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(TryReadError(text) ?? $"Prize Collection failed with HTTP {(int)response.StatusCode}.");

        return PrizeWinModel.ParseResponse(text);
    }

    public async Task<JsonObject?> CommandAsync(string action, object payload, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SiteBaseUrl}/api/staff/command")
        {
            Content = JsonContent(new { action, payload })
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            HandleUnauthorized();
            throw new InvalidOperationException("Your Staff session expired. Sign in again.");
        }
        if (response.StatusCode == (HttpStatusCode)428)
        {
            await ReloadIdentityAfterPasswordRequirementAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Create your personal Staff password before using Staff actions.");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(TryReadError(text) ?? $"Staff command failed with HTTP {(int)response.StatusCode}.");

        var node = JsonNode.Parse(text) as JsonObject;
        await RefreshAsync(false, cancellationToken).ConfigureAwait(false);
        return node;
    }

    private async Task<SnapshotResult> RequestSnapshotAsync(bool force, CancellationToken cancellationToken)
    {
        var query = new List<string> { "client=plugin" };
        if (!string.IsNullOrWhiteSpace(SelectedProfileId))
            query.Add($"profile_id={Uri.EscapeDataString(SelectedProfileId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{SiteBaseUrl}/api/staff/snapshot?{string.Join("&", query)}");
        if (!force && !string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            HandleUnauthorized();
            return SnapshotResult.Unauthorized;
        }
        if (response.StatusCode == (HttpStatusCode)428)
        {
            await ReloadIdentityAfterPasswordRequirementAsync(cancellationToken).ConfigureAwait(false);
            return SnapshotResult.Unauthorized;
        }
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            IsConnected = true;
            LastRefreshUtc = DateTime.UtcNow;
            StatusMessage = Identity is null ? "Connected · no changes" : $"{Identity.DisplayName} · no changes";
            return SnapshotResult.NotModified;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(TryReadError(text) ?? $"Staff snapshot failed with HTTP {(int)response.StatusCode}.");

        var parsed = AdminSnapshot.Parse(text);
        MergePlayerPresence(parsed);
        Snapshot = parsed;
        if (parsed.ActiveProfile is not null) SelectedProfileId = parsed.ActiveProfile.Id;
        etag = response.Headers.ETag?.ToString();
        IsConnected = true;
        LastRefreshUtc = DateTime.UtcNow;
        StatusMessage = Identity is null
            ? $"Connected · API {parsed.ApiVersion}"
            : $"{Identity.DisplayName} · API {parsed.ApiVersion}";
        SnapshotVersion++;
        SnapshotChanged?.Invoke();
        return SnapshotResult.Updated;
    }

    private async Task<StaffIdentityModel> LoadIdentityAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{SiteBaseUrl}/api/staff/me", cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(TryReadError(text) ?? "Staff authentication was not accepted.");
        return StaffIdentityModel.ParseMeResponse(text);
    }

    private async Task ReloadIdentityAfterPasswordRequirementAsync(CancellationToken cancellationToken)
    {
        try
        {
            Identity = await LoadIdentityAsync(cancellationToken).ConfigureAwait(false);
            Snapshot = null;
            IsConnected = false;
            etag = null;
            AuthenticationChanged?.Invoke();
        }
        catch
        {
            HandleUnauthorized();
        }
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated) throw new InvalidOperationException("Staff authentication required. Sign in first.");
        if (RequiresPasswordChange) throw new InvalidOperationException("Create your personal Staff password before continuing.");
    }

    private void HandleUnauthorized() => ClearLocalAuthentication("Staff session expired. Sign in again.");

    private void ClearLocalAuthentication(string status)
    {
        Identity = null;
        Snapshot = null;
        SelectedProfileId = string.Empty;
        etag = null;
        IsConnected = false;
        LastRefreshUtc = null;
        lock (presenceSync) presenceByPlayer.Clear();
        StatusMessage = status;
        AuthenticationChanged?.Invoke();
        SnapshotChanged?.Invoke();
    }

    public void ApplyPlayerPresence(PlayerPresenceRealtimeUpdate update)
    {
        if (!IsAuthenticated) return;
        lock (presenceSync)
        {
            if (update.IsSnapshot) presenceByPlayer.Clear();
            foreach (var entry in update.Players)
            {
                if (string.IsNullOrWhiteSpace(entry.PlayerId)) continue;
                presenceByPlayer[entry.PlayerId] = entry;
            }
        }

        var snapshot = Snapshot;
        if (snapshot is null) return;
        MergePlayerPresence(snapshot);
        SnapshotChanged?.Invoke();
    }

    private void MergePlayerPresence(AdminSnapshot snapshot)
    {
        Dictionary<string, PlayerPresenceEntry> presence;
        lock (presenceSync) presence = new Dictionary<string, PlayerPresenceEntry>(presenceByPlayer, StringComparer.OrdinalIgnoreCase);

        snapshot.Players = snapshot.Players.Select(player =>
        {
            if (!presence.TryGetValue(player.Id, out var live)) return player with { IsOnline = false };
            return player with
            {
                IsOnline = live.IsOnline,
                LastSeenUtc = live.LastSeenUtc ?? player.LastSeenUtc,
            };
        }).ToArray();
    }

    public void ApplySlotsJackpot(JackpotRealtimeUpdate update) => ApplyJackpotUpdate(update, slots: true);
    public void ApplyBlackPrismJackpot(JackpotRealtimeUpdate update) => ApplyJackpotUpdate(update, slots: false);

    private void ApplyJackpotUpdate(JackpotRealtimeUpdate update, bool slots)
    {
        if (!IsAuthenticated) return;
        var snapshot = Snapshot;
        if (snapshot is null) return;

        var amount = Math.Max(5_000_000L, update.Amount);
        if (slots) snapshot.SlotsJackpot = amount;
        else snapshot.BlackPrismJackpot = amount;

        var map = new Dictionary<string, ProfileJackpotTotalModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in snapshot.ProfileJackpotTotals) map[row.ProfileId] = row;

        if (update.ProfilesReset)
        {
            foreach (var key in new List<string>(map.Keys))
            {
                var row = map[key];
                map[key] = slots ? row with { SlotsRaised = 0 } : row with { BlackPrismRaised = 0 };
            }
        }
        else
        {
            foreach (var delta in update.ProfileDeltas)
            {
                if (string.IsNullOrWhiteSpace(delta.ProfileId) || delta.Delta <= 0) continue;
                if (!map.TryGetValue(delta.ProfileId, out var row))
                    row = new ProfileJackpotTotalModel(delta.ProfileId, 0, 0);
                map[delta.ProfileId] = slots
                    ? row with { SlotsRaised = row.SlotsRaised + delta.Delta }
                    : row with { BlackPrismRaised = row.BlackPrismRaised + delta.Delta };
            }
        }

        snapshot.ProfileJackpotTotals = new List<ProfileJackpotTotalModel>(map.Values);
        SnapshotChanged?.Invoke();
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string? TryReadError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) return error.GetString();
            if (root.TryGetProperty("message", out var message)) return message.GetString();
        }
        catch { }
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public void Dispose()
    {
        requestGate.Dispose();
        http.Dispose();
    }

    private enum SnapshotResult { Updated, NotModified, Unauthorized }
}
