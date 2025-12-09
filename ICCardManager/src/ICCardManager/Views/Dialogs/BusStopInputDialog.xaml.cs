using System.Windows;
using ICCardManager.Models;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// バス停入力ダイアログ
/// </summary>
public partial class BusStopInputDialog : Window
{
    private readonly BusStopInputViewModel _viewModel;

    public BusStopInputDialog(BusStopInputViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        // 保存完了時に自動的に閉じる
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(BusStopInputViewModel.IsSaved) && _viewModel.IsSaved)
            {
                DialogResult = true;
                Close();
            }
        };
    }

    /// <summary>
    /// 履歴IDを指定して初期化
    /// </summary>
    public async Task InitializeWithLedgerIdAsync(int ledgerId)
    {
        await _viewModel.InitializeAsync(ledgerId);
    }

    /// <summary>
    /// バス利用詳細を直接指定して初期化（返却時用）
    /// </summary>
    public void InitializeWithDetails(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
    {
        _viewModel.InitializeWithDetails(ledger, busDetails);
    }

    /// <summary>
    /// 保存されたかどうか
    /// </summary>
    public bool IsSaved => _viewModel.IsSaved;
}
