using System;
using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// システム操作による貸出記録作成ダイアログ（Issue #1909）
    /// </summary>
    /// <remarks>
    /// <para>
    /// ViewModel は DI から自前で解決せず、呼び出し元（<c>CardManageViewModel</c>）が
    /// 初期化済みのものを <see cref="Bind"/> で渡す。作成結果の文言
    /// （<see cref="SystemLendViewModel.ResultMessage"/>）を呼び出し元が
    /// カード管理画面のステータス欄へ表示するため、両者が同じインスタンスを見る必要がある。
    /// </para>
    /// <para>
    /// この形にすると、呼び出し元のコマンドが <c>Window</c> を実体化せずに単体テストできる
    /// （ダイアログを DI から解決する形では、成功経路の検証に STA スレッドと実 Window が要る）。
    /// </para>
    /// </remarks>
    public partial class SystemLendDialog : Window
    {
        private SystemLendViewModel _viewModel;

        public SystemLendDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 初期化済みの ViewModel を結び付ける。表示前に必ず 1 度呼ぶこと。
        /// </summary>
        public void Bind(SystemLendViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SystemLendViewModel.IsCompleted) && _viewModel.IsCompleted)
                {
                    DialogResult = true;
                    Close();
                }
            };

            // 借用者の選択から始められるようフォーカスを合わせる
            ContentRendered += (s, e) => BorrowerComboBox.Focus();
        }
    }
}
