using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ICCardManager.Models;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
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
        public async Task InitializeWithDetailsAsync(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
        {
            await _viewModel.InitializeWithDetailsAsync(ledger, busDetails);
        }

        /// <summary>
        /// バス利用詳細を直接指定して初期化（返却時用・同期版 - 後方互換性のため）
        /// </summary>
        public void InitializeWithDetails(Ledger ledger, IEnumerable<LedgerDetail> busDetails)
        {
            // 非同期版を同期的に呼び出し（サジェスト読み込みを含む）
            _ = _viewModel.InitializeWithDetailsAsync(ledger, busDetails);
        }

        /// <summary>
        /// 保存されたかどうか
        /// </summary>
        public bool IsSaved => _viewModel.IsSaved;
    }
}
