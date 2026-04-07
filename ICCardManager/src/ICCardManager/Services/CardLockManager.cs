using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
/// <summary>
    /// カードごとの排他制御ロックを管理するクラス
    /// </summary>
    /// <remarks>
    /// 定期的に未使用のロックをクリーンアップしてメモリリークを防止する
    /// </remarks>
    public class CardLockManager : IDisposable
    {
        /// <summary>
        /// ロックエントリ（SemaphoreSlimと最終使用時刻を保持）
        /// </summary>
        private class LockEntry
        {
            public SemaphoreSlim Semaphore { get; }
            public DateTime LastUsed { get; set; }
            public int ReferenceCount { get; set; }

            /// <summary>
            /// Issue #1171: クリーンアップによりDispose済みかどうか。
            /// GetLockがこのフラグをチェックし、true の場合は辞書から取り除いて再試行する。
            /// 必ず lock(this) 内で読み書きすること。
            /// </summary>
            public bool IsDisposed { get; set; }

            public LockEntry()
            {
                Semaphore = new SemaphoreSlim(1, 1);
                LastUsed = DateTime.UtcNow;
                ReferenceCount = 0;
            }
        }

        private readonly ConcurrentDictionary<string, LockEntry> _locks = new();
        private readonly ILogger<CardLockManager> _logger;
        private readonly Timer _cleanupTimer;
        private readonly object _cleanupLock = new();
        private bool _disposed;

        /// <summary>
        /// ロックの有効期限（デフォルト: 1時間）
        /// </summary>
        public TimeSpan LockExpiration { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// クリーンアップ間隔（デフォルト: 15分）
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// 現在のロック数
        /// </summary>
        public int LockCount => _locks.Count;

        public CardLockManager(ILogger<CardLockManager> logger)
        {
            _logger = logger;

            // クリーンアップタイマーを開始
            _cleanupTimer = new Timer(
                CleanupCallback,
                null,
                CleanupInterval,
                CleanupInterval);

            _logger.LogDebug("CardLockManagerを初期化しました（クリーンアップ間隔: {Interval}分）",
                CleanupInterval.TotalMinutes);
        }

        /// <summary>
        /// 指定されたカードIDmのロックを取得
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <returns>SemaphoreSlim</returns>
        /// <remarks>
        /// Issue #1171: GetLockとCleanupExpiredLocksのTOCTOU競合を回避するため、
        /// 取得したエントリがクリーンアップによってDispose済みの場合はリトライする。
        /// </remarks>
        public SemaphoreSlim GetLock(string cardIdm)
        {
            while (true)
            {
                var entry = _locks.GetOrAdd(cardIdm, _ => new LockEntry());

                // 最終使用時刻を更新し、参照カウントをインクリメント
                lock (entry)
                {
                    // Issue #1171: クリーンアップによりDispose済みなら、辞書から取り除いて再試行
                    // TryRemove(KVP)は値が一致する場合のみ削除する原子操作のため、
                    // 別スレッドが同じキーで新エントリを既に登録していた場合に誤削除されない
                    if (entry.IsDisposed)
                    {
                        ((ICollection<KeyValuePair<string, LockEntry>>)_locks).Remove(new KeyValuePair<string, LockEntry>(cardIdm, entry));
                        continue;
                    }

                    entry.LastUsed = DateTime.UtcNow;
                    entry.ReferenceCount++;
                    return entry.Semaphore;
                }
            }
        }

        /// <summary>
        /// ロックの使用完了を通知
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        public void ReleaseLockReference(string cardIdm)
        {
            if (_locks.TryGetValue(cardIdm, out var entry))
            {
                lock (entry)
                {
                    entry.ReferenceCount = Math.Max(0, entry.ReferenceCount - 1);
                    entry.LastUsed = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// タイマーコールバック：定期クリーンアップを実行
        /// </summary>
        private void CleanupCallback(object state)
        {
            if (_disposed) return;

            try
            {
                CleanupExpiredLocks();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ロッククリーンアップ中にエラーが発生しました");
            }
        }

        /// <summary>
        /// 期限切れのロックをクリーンアップ
        /// </summary>
        /// <remarks>
        /// Issue #1171: マーク・削除・Dispose を単一の lock(entry) クリティカルセクション内で実行する。
        /// これにより GetLock との TOCTOU 競合を排除し、ObjectDisposedException を防止する。
        /// </remarks>
        public void CleanupExpiredLocks()
        {
            lock (_cleanupLock)
            {
                var cutoffTime = DateTime.UtcNow - LockExpiration;
                var removedCount = 0;

                // 列挙中の変更を避けるためスナップショットを取る
                foreach (var kvp in _locks.ToArray())
                {
                    var cardIdm = kvp.Key;
                    var entry = kvp.Value;

                    // Issue #1171: 単一クリティカルセクションでチェック→マーク→削除を実行
                    lock (entry)
                    {
                        // 参照カウントが0で、最終使用時刻が期限切れで、Semaphoreが使用中でない場合のみ削除
                        if (entry.ReferenceCount != 0 ||
                            entry.LastUsed >= cutoffTime ||
                            entry.Semaphore.CurrentCount != 1)
                        {
                            continue;
                        }

                        // Dispose済みフラグを立て、辞書から原子的に削除
                        // TryRemove(KVP)は値が一致する場合のみ削除するため、
                        // 別スレッドが同じキーで既に置き換えていた場合は誤削除されない
                        entry.IsDisposed = true;
                        var removed = ((ICollection<KeyValuePair<string, LockEntry>>)_locks).Remove(new KeyValuePair<string, LockEntry>(cardIdm, entry));
                        if (removed)
                        {
                            try
                            {
                                entry.Semaphore.Dispose();
                            }
                            catch (ObjectDisposedException)
                            {
                                // 既にDisposeされている場合は無視
                            }
                            removedCount++;
                        }
                        else
                        {
                            // 別スレッドが既に置き換えていた場合は IsDisposed を取り消す
                            // （新エントリは別インスタンスなので副作用なし）
                            entry.IsDisposed = false;
                        }
                    }
                }

                if (removedCount > 0)
                {
                    _logger.LogDebug(
                        "未使用のロックをクリーンアップしました: {Count}件削除、残り{Remaining}件",
                        removedCount,
                        _locks.Count);
                }
            }
        }

        /// <summary>
        /// 強制的にすべてのロックをクリア（シャットダウン時用）
        /// </summary>
        public void ClearAllLocks()
        {
            lock (_cleanupLock)
            {
                var count = _locks.Count;

                foreach (var kvp in _locks)
                {
                    try
                    {
                        kvp.Value.Semaphore.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 既にDisposeされている場合は無視
                    }
                }

                _locks.Clear();

                _logger.LogDebug("すべてのロックをクリアしました: {Count}件", count);
            }
        }

        /// <summary>
        /// 特定のカードIDmのロックを削除（テスト用）
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <returns>削除成功した場合はtrue</returns>
        public bool RemoveLock(string cardIdm)
        {
            if (_locks.TryRemove(cardIdm, out var entry))
            {
                try
                {
                    entry.Semaphore.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // 既にDisposeされている場合は無視
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 指定されたカードIDmのロックが存在するかチェック
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <returns>存在する場合はtrue</returns>
        public bool HasLock(string cardIdm)
        {
            return _locks.ContainsKey(cardIdm);
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
                // タイマーを停止
                _cleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _cleanupTimer.Dispose();

                // すべてのロックをクリア
                ClearAllLocks();

                _logger.LogDebug("CardLockManagerをDisposeしました");
            }

            _disposed = true;
        }
    }
}
