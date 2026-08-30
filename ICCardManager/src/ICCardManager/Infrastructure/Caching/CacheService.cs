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

            await keyLock.WaitAsync().ConfigureAwait(false);
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
                var value = await factory().ConfigureAwait(false);

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
            options.RegisterPostEvictionCallback(OnPostEviction);

            _cache.Set(key, value, options);
            _keys.TryAdd(key, 0);

            _logger.LogTrace("キャッシュ設定: {Key} (有効期限: {Seconds}秒)", key, absoluteExpiration.TotalSeconds);
        }

        /// <summary>
        /// キャッシュエントリが退避されたときに呼ばれ、キー追跡表（<see cref="_keys"/>）を更新する。
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #1943: <see cref="IMemoryCache"/> はこのコールバックを<b>スレッドプールで実行する</b>ため、
        /// 呼び出しは退避の契機となった操作より後に着地し得る。したがって理由を見ずに追跡表から削除すると、
        /// 同じキーへの再 <see cref="Set{T}"/>（<see cref="EvictionReason.Replaced"/>）や
        /// <see cref="Invalidate"/> 直後の再 <see cref="Set{T}"/>（<see cref="EvictionReason.Removed"/>）で、
        /// <b>キャッシュには生きているのに追跡表から落ちたキー</b>が生まれる。
        /// <see cref="InvalidateByPrefix"/> はこの表しか走査しないため、
        /// 削除済みのカード・職員が TTL いっぱい一覧に残り続ける
        /// （Issue #1759 が「影響行数 0 のときこそキャッシュを無効化する」で防ごうとした状態）。
        /// </para>
        /// <para>
        /// 判定は「削除する理由」を列挙する側（ホワイトリスト）で書く。未知の理由が増えたとき、
        /// 削除側の列挙なら「追跡表に残る」＝<see cref="InvalidateByPrefix"/> が空振りの
        /// <see cref="IMemoryCache.Remove"/> を 1 回多く呼ぶだけで済むが、
        /// 除外側の列挙（<see cref="EvictionReason.Replaced"/> だけを弾く形）では本欠陥が再発する。
        /// </para>
        /// </remarks>
        internal void OnPostEviction(object evictedKey, object? value, EvictionReason reason, object? state)
        {
            // キャッシュ都合で実際にエントリが失われた退避だけを追跡表へ反映する。
            // Replaced / Removed / None は「同じキーの新しいエントリが生きている」または
            // 「Invalidate・Clear が同期で追跡表を更新済み」であり、ここで消してはならない。
            if (reason is not (EvictionReason.Expired or EvictionReason.Capacity or EvictionReason.TokenExpired))
            {
                return;
            }

            _keys.TryRemove(evictedKey.ToString()!, out _);
            _logger.LogTrace("キャッシュ退避: {Key} (理由: {Reason})", evictedKey, reason);
        }

        /// <summary>
        /// 追跡中のキー一覧。
        /// </summary>
        /// <remarks>
        /// <see cref="InvalidateByPrefix"/> はこの表だけを走査するため、
        /// 「キャッシュには生きているのに表から落ちたキー」を回帰テストで直接表明できるようにしている。
        /// </remarks>
        internal IReadOnlyCollection<string> TrackedKeys => _keys.Keys.ToArray();

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
