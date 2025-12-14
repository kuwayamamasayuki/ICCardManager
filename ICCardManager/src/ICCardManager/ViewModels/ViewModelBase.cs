using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ICCardManager.ViewModels;

/// <summary>
/// ViewModelの基底クラス
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _busyMessage;
    private bool _canCancel;
    private double _progressValue;
    private double _progressMax = 100;
    private bool _isIndeterminate = true;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// 処理中かどうか
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// 処理中のメッセージ
    /// </summary>
    public string? BusyMessage
    {
        get => _busyMessage;
        set => SetProperty(ref _busyMessage, value);
    }

    /// <summary>
    /// キャンセル可能かどうか
    /// </summary>
    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    /// <summary>
    /// 進捗値
    /// </summary>
    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    /// <summary>
    /// 進捗最大値
    /// </summary>
    public double ProgressMax
    {
        get => _progressMax;
        set => SetProperty(ref _progressMax, value);
    }

    /// <summary>
    /// 不定プログレス（進捗不明）かどうか
    /// </summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => SetProperty(ref _isIndeterminate, value);
    }

    /// <summary>
    /// キャンセルが要求されているか
    /// </summary>
    public bool IsCancellationRequested => _cancellationTokenSource?.IsCancellationRequested ?? false;

    /// <summary>
    /// 処理中状態を設定
    /// </summary>
    protected void SetBusy(bool isBusy, string? message = null)
    {
        IsBusy = isBusy;
        BusyMessage = message;

        if (!isBusy)
        {
            ResetProgress();
        }
    }

    /// <summary>
    /// 進捗をリセット
    /// </summary>
    protected void ResetProgress()
    {
        ProgressValue = 0;
        ProgressMax = 100;
        IsIndeterminate = true;
        CanCancel = false;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    /// <summary>
    /// 進捗を設定（確定プログレス）
    /// </summary>
    protected void SetProgress(double value, double max, string? message = null)
    {
        IsIndeterminate = false;
        ProgressValue = value;
        ProgressMax = max;
        if (message != null)
        {
            BusyMessage = message;
        }
    }

    /// <summary>
    /// 処理中にスコープを作成
    /// </summary>
    protected IDisposable BeginBusy(string? message = null)
    {
        return new BusyScope(this, message, false);
    }

    /// <summary>
    /// キャンセル可能な処理中スコープを作成
    /// </summary>
    protected BusyScope BeginCancellableBusy(string? message = null)
    {
        return new BusyScope(this, message, true);
    }

    /// <summary>
    /// キャンセルコマンド
    /// </summary>
    [RelayCommand]
    public void CancelOperation()
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
            BusyMessage = "キャンセル中...";
            System.Diagnostics.Debug.WriteLine("[UI] 操作がキャンセルされました");
        }
    }

    /// <summary>
    /// Busy状態のスコープ
    /// </summary>
    protected class BusyScope : IDisposable
    {
        private readonly ViewModelBase _viewModel;
        private bool _disposed;

        /// <summary>
        /// キャンセルトークン
        /// </summary>
        public CancellationToken CancellationToken { get; }

        public BusyScope(ViewModelBase viewModel, string? message, bool canCancel)
        {
            _viewModel = viewModel;
            _viewModel.SetBusy(true, message);
            _viewModel.CanCancel = canCancel;

            if (canCancel)
            {
                _viewModel._cancellationTokenSource = new CancellationTokenSource();
                CancellationToken = _viewModel._cancellationTokenSource.Token;
            }
            else
            {
                CancellationToken = CancellationToken.None;
            }
        }

        /// <summary>
        /// キャンセルが要求されているかチェック（例外をスロー）
        /// </summary>
        public void ThrowIfCancellationRequested()
        {
            CancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// 進捗を更新
        /// </summary>
        public void ReportProgress(double value, double max, string? message = null)
        {
            _viewModel.SetProgress(value, max, message);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _viewModel.SetBusy(false);
                _disposed = true;
            }
        }
    }
}
