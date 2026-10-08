#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 繰越情報の復旧ダイアログ（Issue #2255）
    /// </summary>
    /// <remarks>
    /// 繰越情報消失一覧ダイアログの行の「復旧...」から開く。保存すると <c>DialogResult = true</c> で閉じ、
    /// 一覧が作り直される。
    /// </remarks>
    public partial class CarryoverRecoveryDialog : Window
    {
        private readonly CarryoverRecoveryViewModel _viewModel;

        /// <summary>最初にフォーカスを置く入力欄（失われた項目のうち先頭のもの）</summary>
        private CarryoverInputField _initialFocusField = CarryoverInputField.StartingPageNumber;

        /// <summary>復旧の対象（<see cref="SetTarget"/> で受け取り、表示後に読み込む）</summary>
        private CarryoverDataLossItem? _target;

        public CarryoverRecoveryDialog(CarryoverRecoveryViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;

            // 保存完了で閉じる
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(CarryoverRecoveryViewModel.IsSaved) && _viewModel.IsSaved)
                {
                    DialogResult = true;
                    Close();
                }
            };

            // 入力の誤りがあった欄へフォーカスを移す（どの欄を直せばよいかを、文言だけでなく位置でも示す）
            _viewModel.FocusRequested += (s, field) => FocusField(field);

            // カードの読み込みは表示した後に行う。表示前（ShowDialogAsync の初期化）で待つと、その間は
            // まだモーダルが無く、一覧ダイアログを閉じられる。閉じた時点で警告の再判定が先に終わり、
            // その後に開いたこのダイアログで復旧しても、メイン画面の警告が残る。
            Loaded += async (s, e) =>
            {
                try
                {
                    await LoadTargetAsync();
                    FocusField(_initialFocusField);
                }
                catch (Exception ex)
                {
                    // ViewModel は読み込みの失敗を画面に案内して例外にしない。ここへ来るのは想定外の失敗だけで、
                    // async void から抜けると未処理例外になるため痕跡を残して止める（保存できない状態のまま）。
                    ErrorDialogHelper.LogException(ex, "繰越情報の復旧画面の読み込み");
                }
            };
        }

        /// <summary>
        /// 復旧する行の検出結果を受け取る（読み込みは表示した後に行う）
        /// </summary>
        /// <param name="target">一覧で選んだ行の検出結果</param>
        public void SetTarget(CarryoverDataLossItem target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _initialFocusField = ResolveInitialFocusField(target);
        }

        /// <summary>
        /// 受け取った対象でカードを読み込み、入力欄を用意する
        /// </summary>
        internal Task LoadTargetAsync() =>
            _target == null ? Task.CompletedTask : _viewModel.InitializeAsync(_target);

        /// <summary>
        /// 最初にフォーカスを置く入力欄を決める（失われた項目のうち先頭のもの）
        /// </summary>
        internal static CarryoverInputField ResolveInitialFocusField(CarryoverDataLossItem target)
        {
            if (target.LostStartingPageNumber.HasValue)
            {
                return CarryoverInputField.StartingPageNumber;
            }

            if (target.LostCarryoverIncomeTotal.HasValue)
            {
                return CarryoverInputField.CarryoverIncomeTotal;
            }

            if (target.LostCarryoverExpenseTotal.HasValue)
            {
                return CarryoverInputField.CarryoverExpenseTotal;
            }

            return target.LostCarryoverFiscalYear.HasValue
                ? CarryoverInputField.CarryoverFiscalYear
                : CarryoverInputField.StartingPageNumber;
        }

        private void FocusField(CarryoverInputField field)
        {
            TextBox textBox = field switch
            {
                CarryoverInputField.CarryoverIncomeTotal => CarryoverIncomeTotalTextBox,
                CarryoverInputField.CarryoverExpenseTotal => CarryoverExpenseTotalTextBox,
                CarryoverInputField.CarryoverFiscalYear => CarryoverFiscalYearTextBox,
                _ => StartingPageNumberTextBox,
            };

            textBox.Focus();
            textBox.SelectAll();
        }
    }
}
