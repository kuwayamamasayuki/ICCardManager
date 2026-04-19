using System;
using System.Threading.Tasks;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Services
{
    /// <summary>
    /// 共有フォルダモードでのDB接続監視と同期表示を担当するサービス
    /// </summary>
    /// <remarks>
    /// MainViewModelから抽出。30秒ごとのDB接続ヘルスチェックと
    /// 1秒ごとの同期経過時間表示を管理する。
    /// </remarks>
    public class SharedModeMonitor : IDisposable
    {
        private readonly IDatabaseInfo _databaseInfo;
        private readonly ITimerFactory _timerFactory;
        private readonly ISystemClock _clock;

        private ITimer _healthCheckTimer;
        private ITimer _syncDisplayTimer;
        private DateTime? _lastRefreshTime;
        private bool _isHealthCheckRunning;
        private bool _disposed;

        /// <summary>
        /// 最終同期からの経過がしきい値を超えた場合にstaleとみなす秒数
        /// </summary>
        internal const int StaleThresholdSeconds = 15;

        /// <summary>
        /// DB接続チェック結果のイベント
        /// </summary>
        public event EventHandler<DatabaseHealthEventArgs> HealthCheckCompleted;

        /// <summary>
        /// 同期表示テキストが更新されたときのイベント
        /// </summary>
        public event EventHandler<SyncDisplayEventArgs> SyncDisplayUpdated;

        public SharedModeMonitor(IDatabaseInfo databaseInfo, ITimerFactory timerFactory, ISystemClock clock)
        {
            _databaseInfo = databaseInfo ?? throw new ArgumentNullException(nameof(databaseInfo));
            _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// 監視を開始する
        /// </summary>
        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SharedModeMonitor));
            }

            Stop();

            _healthCheckTimer = _timerFactory.Create();
            _healthCheckTimer.Interval = TimeSpan.FromSeconds(30);
            _healthCheckTimer.Tick += OnHealthCheckTick;
            _healthCheckTimer.Start();

            // Issue #1131: 同期経過時間の表示更新用タイマー（1秒間隔）
            _syncDisplayTimer = _timerFactory.Create();
            _syncDisplayTimer.Interval = TimeSpan.FromSeconds(1);
            _syncDisplayTimer.Tick += OnSyncDisplayTick;
            _syncDisplayTimer.Start();
        }

        /// <summary>
        /// 監視を停止する
        /// </summary>
        public void Stop()
        {
            if (_healthCheckTimer != null)
            {
                _healthCheckTimer.Stop();
                _healthCheckTimer.Tick -= OnHealthCheckTick;
                _healthCheckTimer = null;
            }

            if (_syncDisplayTimer != null)
            {
                _syncDisplayTimer.Stop();
                _syncDisplayTimer.Tick -= OnSyncDisplayTick;
                _syncDisplayTimer = null;
            }
        }

        /// <summary>
        /// タイマーを停止してインスタンスを破棄する（Issue #1286）。
        /// 複数回呼び出しても安全（冪等）。Dispose 後の <see cref="Start"/> は
        /// <see cref="ObjectDisposedException"/> を投げる。
        /// </summary>
        /// <remarks>
        /// 通常のライフサイクル内の停止は <see cref="Stop"/> を使い、
        /// アプリ終了などの破棄時のみ Dispose を呼ぶこと。
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }

        /// <summary>
        /// データの同期が完了したことを記録する
        /// </summary>
        public void RecordRefresh()
        {
            _lastRefreshTime = _clock.Now;
            UpdateSyncDisplayText();
        }

        /// <summary>
        /// ヘルスチェックが実行中かどうか
        /// </summary>
        public bool IsHealthCheckRunning => _isHealthCheckRunning;

        /// <summary>
        /// ヘルスチェックの実行中フラグを設定する（手動リフレッシュ時に使用）
        /// </summary>
        internal void SetHealthCheckRunning(bool value)
        {
            _isHealthCheckRunning = value;
        }

        /// <summary>
        /// DB接続の疎通確認をバックグラウンドで実行する
        /// </summary>
        public async Task<bool> CheckConnectionAsync()
        {
            return await Task.Run(() => _databaseInfo.CheckConnection());
        }

        /// <summary>
        /// Issue #1131: 最終同期からの経過時間をテキストとして更新
        /// </summary>
        internal void UpdateSyncDisplayText()
        {
            if (_lastRefreshTime == null)
            {
                SyncDisplayUpdated?.Invoke(this, new SyncDisplayEventArgs("同期待ち...", false));
                return;
            }

            var elapsed = (int)(_clock.Now - _lastRefreshTime.Value).TotalSeconds;
            string text;
            if (elapsed < 5)
            {
                text = "最終同期: たった今";
            }
            else if (elapsed < 60)
            {
                text = $"最終同期: {elapsed}秒前";
            }
            else
            {
                var minutes = elapsed / 60;
                text = $"最終同期: {minutes}分前";
            }

            SyncDisplayUpdated?.Invoke(this, new SyncDisplayEventArgs(text, elapsed >= StaleThresholdSeconds));
        }

        /// <summary>
        /// ヘルスチェックを実行する。排他制御により、実行中の場合は何もせず終了する。
        /// Timer Tick から呼ばれるほか、テストから同期的に排他制御を検証できるよう internal 公開。
        /// </summary>
        /// <returns>
        /// 実行された場合は true、既に実行中(または手動リフレッシュ中)で
        /// スキップされた場合は false。
        /// </returns>
        internal async Task<bool> ExecuteHealthCheckAsync()
        {
            if (_isHealthCheckRunning)
                return false;

            _isHealthCheckRunning = true;
            try
            {
                var isConnected = await CheckConnectionAsync();
                HealthCheckCompleted?.Invoke(this, new DatabaseHealthEventArgs(isConnected));
                return true;
            }
            finally
            {
                _isHealthCheckRunning = false;
            }
        }

        private async void OnHealthCheckTick(object sender, EventArgs e)
        {
            // async void は例外が伝播しないため、排他制御ロジックは ExecuteHealthCheckAsync に集約
            await ExecuteHealthCheckAsync();
        }

        private void OnSyncDisplayTick(object sender, EventArgs e)
        {
            UpdateSyncDisplayText();
        }
    }

    /// <summary>
    /// DB接続ヘルスチェック結果のイベント引数
    /// </summary>
    public class DatabaseHealthEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public DatabaseHealthEventArgs(bool isConnected) => IsConnected = isConnected;
    }

    /// <summary>
    /// 同期表示更新のイベント引数
    /// </summary>
    public class SyncDisplayEventArgs : EventArgs
    {
        public string Text { get; }
        public bool IsStale { get; }
        public SyncDisplayEventArgs(string text, bool isStale)
        {
            Text = text;
            IsStale = isStale;
        }
    }
}
