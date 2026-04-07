using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Infrastructure.Caching
{
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

        /// <summary>
        /// Issue #1167: GetOrCreateAsyncのキーごとの排他制御用セマフォ。
        /// ダブルチェックロッキングで factory() の多重実行を防止する。
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

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
            // 1段目チェック（ロック取得前の高速パス）
            if (_cache.TryGetValue(key, out T? cachedValue) && cachedValue is not null)
            {
                _logger.LogTrace("キャッシュヒット: {Key}", key);
                return cachedValue;
            }

            // Issue #1167: ダブルチェックロッキング
            // キーごとのセマフォで factory() の多重実行を防止する。
            // 複数の並行呼び出しが同時にキャッシュミスした場合、最初の1回だけ
            // factory() を実行し、残りはキャッシュ済みの結果を取得する。
            var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await keyLock.WaitAsync();
            try
            {
                // 2段目チェック（ロック取得後）
                if (_cache.TryGetValue(key, out T? doubleCheckedValue) && doubleCheckedValue is not null)
                {
                    _logger.LogTrace("キャッシュヒット（ロック後）: {Key}", key);
                    return doubleCheckedValue;
                }

                // キャッシュミス - ファクトリを実行
                _logger.LogTrace("キャッシュミス: {Key}", key);
                var value = await factory();

                Set(key, value, absoluteExpiration);

                return value;
            }
            finally
            {
                keyLock.Release();
            }
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
                // Issue #1167: キーごとのセマフォを破棄
                foreach (var keyLock in _keyLocks.Values)
                {
                    keyLock.Dispose();
                }
                _keyLocks.Clear();
            }

            _disposed = true;
        }
    }
}
