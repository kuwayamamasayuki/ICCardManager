using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services;

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
    public SemaphoreSlim GetLock(string cardIdm)
    {
        var entry = _locks.GetOrAdd(cardIdm, _ => new LockEntry());

        // 最終使用時刻を更新し、参照カウントをインクリメント
        lock (entry)
        {
            entry.LastUsed = DateTime.UtcNow;
            entry.ReferenceCount++;
        }

        return entry.Semaphore;
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
    private void CleanupCallback(object? state)
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
    public void CleanupExpiredLocks()
    {
        lock (_cleanupLock)
        {
            var cutoffTime = DateTime.UtcNow - LockExpiration;
            var keysToRemove = new List<string>();

            foreach (var kvp in _locks)
            {
                var cardIdm = kvp.Key;
                var entry = kvp.Value;

                // ロックされた状態でチェック
                lock (entry)
                {
                    // 参照カウントが0で、最終使用時刻が期限切れの場合のみ削除
                    if (entry.ReferenceCount == 0 && entry.LastUsed < cutoffTime)
                    {
                        // SemaphoreSlimが使用中でないことを確認
                        if (entry.Semaphore.CurrentCount == 1)
                        {
                            keysToRemove.Add(cardIdm);
                        }
                    }
                }
            }

            // 削除対象のロックを削除
            foreach (var key in keysToRemove)
            {
                if (_locks.TryRemove(key, out var removedEntry))
                {
                    // SemaphoreSlimをDispose
                    try
                    {
                        removedEntry.Semaphore.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 既にDisposeされている場合は無視
                    }
                }
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogDebug(
                    "未使用のロックをクリーンアップしました: {Count}件削除、残り{Remaining}件",
                    keysToRemove.Count,
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
