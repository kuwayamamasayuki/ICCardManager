using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Infrastructure.Caching;

/// <summary>
/// メモリキャッシュを使用したキャッシュサービス実装
/// </summary>
public class CacheService : ICacheService, IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _keys;
    private readonly ILogger<CacheService> _logger;
    private readonly object _lock = new();
    private bool _disposed;

    public CacheService(ILogger<CacheService> logger)
    {
        _logger = logger;
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
            _logger.LogTrace("キャッシュヒット: {Key}", key);
            return cachedValue;
        }

        // キャッシュミス - ファクトリを実行
        _logger.LogTrace("キャッシュミス: {Key}", key);
        var value = await factory();

        Set(key, value, absoluteExpiration);

        return value;
    }

    /// <inheritdoc/>
    public T? Get<T>(string key)
    {
        if (_cache.TryGetValue(key, out T? value))
        {
            _logger.LogTrace("キャッシュヒット: {Key}", key);
            return value;
        }

        _logger.LogTrace("キャッシュミス: {Key}", key);
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
            _logger.LogTrace("キャッシュ期限切れ: {Key}", evictedKey);
        });

        _cache.Set(key, value, options);
        _keys.TryAdd(key, 0);

        _logger.LogTrace("キャッシュ設定: {Key} (有効期限: {Seconds}秒)", key, absoluteExpiration.TotalSeconds);
    }

    /// <inheritdoc/>
    public void Invalidate(string key)
    {
        lock (_lock)
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
            _logger.LogDebug("キャッシュ無効化: {Key}", key);
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

            _logger.LogDebug("プレフィックスでキャッシュ無効化: {Prefix} ({Count}件)", prefix, keysToRemove.Count);
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
        {
            var count = _keys.Count;
            foreach (var key in _keys.Keys.ToList())
            {
                _cache.Remove(key);
            }
            _keys.Clear();
            _logger.LogInformation("全キャッシュをクリア ({Count}件)", count);
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
