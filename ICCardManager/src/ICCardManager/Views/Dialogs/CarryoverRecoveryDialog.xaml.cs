#nullable enable
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
    /// 繰越情報消失一覧ダイアログの行の「復旧」から開く。保存すると <c>DialogResult = true</c> で閉じ、
    /// 一覧が作り直される。
    /// </remarks>
    public partial class CarryoverRecoveryDialog : Window
    {
        private readonly CarryoverRecoveryViewModel _viewModel;

        /// <summary>最初にフォーカスを置く入力欄（失われた項目のうち先頭のもの）</summary>
        private CarryoverInputField _initialFocusField = CarryoverInputField.StartingPageNumber;

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

            ContentRendered += (s, e) => FocusField(_initialFocusField);
        }

        /// <summary>
        /// 復旧する行の検出結果を渡して初期化する
        /// </summary>
        /// <param name="target">一覧で選んだ行の検出結果</param>
        public Task InitializeAsync(CarryoverDataLossItem target)
        {
            _initialFocusField = ResolveInitialFocusField(target);
            return _viewModel.InitializeAsync(target);
        }

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
