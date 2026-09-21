using Microsoft.Extensions.Caching.Memory;

namespace NebulaBridge.Services;

/// <summary>
/// Owns Nebula Bridge's transient state. Keeping this separate from Jellyfin's shared
/// application cache lets plugin maintenance clear its own entries without evicting
/// unrelated server or third-party-plugin data.
/// </summary>
public sealed class NebulaBridgeCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public T? Get<T>(string key) => _cache.Get<T>(key);

    public bool TryGetValue<T>(string key, out T? value) =>
        _cache.TryGetValue(key, out value);

    public void Set<T>(string key, T value, TimeSpan lifetime) =>
        _cache.Set(key, value, lifetime);

    public void Remove(string key) => _cache.Remove(key);

    public void Clear() => _cache.Compact(1.0);

    public void Dispose() => _cache.Dispose();
}
