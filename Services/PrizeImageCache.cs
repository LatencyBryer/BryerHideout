using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace BryersHideoutPlugin.Services;

public sealed class PrizeImageCache : IDisposable
{
    private sealed class Entry
    {
        public IDalamudTextureWrap? Texture;
        public Task? Loading;
        public string? Error;
    }

    private readonly ITextureProvider textures;
    private readonly IPluginLog log;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource disposeCts = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private bool disposed;

    public PrizeImageCache(ITextureProvider textures, IPluginLog log)
    {
        this.textures = textures;
        this.log = log;
    }

    public IDalamudTextureWrap? GetOrRequest(string? iconPath)
    {
        if (disposed || string.IsNullOrWhiteSpace(iconPath)) return null;
        var key = iconPath.Trim();
        Entry entry;
        lock (sync)
        {
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                entries[key] = entry;
            }

            if (entry.Texture is not null) return entry.Texture;
            if (entry.Loading is null && entry.Error is null)
                entry.Loading = LoadAsync(key, entry, disposeCts.Token);
        }
        return null;
    }

    private async Task LoadAsync(string key, Entry entry, CancellationToken cancellationToken)
    {
        try
        {
            var url = ResolveUrl(key);
            var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            var texture = await textures.CreateFromImageAsync(bytes, $"BryersHideout prize {key}", cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed)
                {
                    texture.Dispose();
                    return;
                }
                entry.Texture = texture;
                entry.Loading = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
            {
                entry.Error = ex.Message;
                entry.Loading = null;
            }
            log.Debug(ex, "Could not load Prize Collection image {IconPath}", key);
        }
    }

    private static string ResolveUrl(string iconPath)
    {
        if (Uri.TryCreate(iconPath, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return absolute.ToString();
        return $"{AdminApiClient.SiteBaseUrl}/{iconPath.TrimStart('/')}";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        disposeCts.Cancel();
        lock (sync)
        {
            foreach (var entry in entries.Values)
            {
                try { entry.Texture?.Dispose(); } catch { }
            }
            entries.Clear();
        }
        http.Dispose();
        disposeCts.Dispose();
    }
}
