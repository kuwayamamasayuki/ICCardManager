using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace ICCardManager.Infrastructure.Caching;

/// <summary>
/// メモリキャッシュを使用したキャッシュサービス実装
/// </summary>
public class CacheService : ICacheService, IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _keys;
    private readonly object _lock = new();
    private bool _disposed;

    public CacheService()
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            // サイズ制限は設定しない（小規模アプリケーションのため）
            SizeLimit = null
        });
        _keys = new ConcurrentDictionary<string, byte>();
    }

    /// <inheritdoc/>
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan absoluteExpiration)
    {
        if (_cache.TryGetValue(key, out T? cachedValue) && cachedValue is not null)
        {
            System.Diagnostics.Debug.WriteLine($"[Cache] HIT: {key}");
            return cachedValue;
        }

        // キャッシュミス - ファクトリを実行
        System.Diagnostics.Debug.WriteLine($"[Cache] MISS: {key}");
        var value = await factory();

        Set(key, value, absoluteExpiration);

        return value;
    }

    /// <inheritdoc/>
    public T? Get<T>(string key)
    {
        if (_cache.TryGetValue(key, out T? value))
        {
            System.Diagnostics.Debug.WriteLine($"[Cache] HIT: {key}");
            return value;
        }

        System.Diagnostics.Debug.WriteLine($"[Cache] MISS: {key}");
        return default;
    }

    /// <inheritdoc/>
    public void Set<T>(string key, T value, TimeSpan absoluteExpiration)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = absoluteExpiration
        };

        // キー追跡のためのコールバックを設定
        options.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            _keys.TryRemove(evictedKey.ToString()!, out _);
            System.Diagnostics.Debug.WriteLine($"[Cache] EVICTED: {evictedKey}");
        });

        _cache.Set(key, value, options);
        _keys.TryAdd(key, 0);

        System.Diagnostics.Debug.WriteLine($"[Cache] SET: {key} (expires in {absoluteExpiration.TotalSeconds}s)");
    }

    /// <inheritdoc/>
    public void Invalidate(string key)
    {
        lock (_lock)
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
            System.Diagnostics.Debug.WriteLine($"[Cache] INVALIDATED: {key}");
        }
    }

    /// <inheritdoc/>
    public void InvalidateByPrefix(string prefix)
    {
        lock (_lock)
        {
            var keysToRemove = _keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.Remove(key);
                _keys.TryRemove(key, out _);
            }

            System.Diagnostics.Debug.WriteLine($"[Cache] INVALIDATED by prefix '{prefix}': {keysToRemove.Count} items");
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var key in _keys.Keys.ToList())
            {
                _cache.Remove(key);
            }
            _keys.Clear();
            System.Diagnostics.Debug.WriteLine("[Cache] CLEARED all items");
        }
    }

    /// <summary>
    /// リソースを解放
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _cache.Dispose();
        }

        _disposed = true;
    }
}
