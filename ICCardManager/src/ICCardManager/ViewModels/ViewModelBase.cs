using CommunityToolkit.Mvvm.ComponentModel;

namespace ICCardManager.ViewModels;

/// <summary>
/// ViewModelの基底クラス
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _busyMessage;

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
    /// 処理中状態を設定
    /// </summary>
    protected void SetBusy(bool isBusy, string? message = null)
    {
        IsBusy = isBusy;
        BusyMessage = message;
    }

    /// <summary>
    /// 処理中にスコープを作成
    /// </summary>
    protected IDisposable BeginBusy(string? message = null)
    {
        return new BusyScope(this, message);
    }

    /// <summary>
    /// Busy状態のスコープ
    /// </summary>
    private class BusyScope : IDisposable
    {
        private readonly ViewModelBase _viewModel;

        public BusyScope(ViewModelBase viewModel, string? message)
        {
            _viewModel = viewModel;
            _viewModel.SetBusy(true, message);
        }

        public void Dispose()
        {
            _viewModel.SetBusy(false);
        }
    }
}
