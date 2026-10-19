#nullable enable
using System;
using System.Windows;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// カードの履歴からの取り込みダイアログ（Issue #2293）
    /// </summary>
    /// <remarks>
    /// 残額の食い違い警告（貸出中でないカード）から開いた履歴の「カードの履歴から取り込む...」で開く。
    /// 取り込んだかどうかは <see cref="ViewModel"/> の <see cref="CardHistoryImportViewModel.HasImported"/> で判定する
    /// （取り込んだ後は結果を読ませるため開いたままにし、✕ / Esc で閉じても取り込みは確定しているため、
    /// <c>DialogResult</c> では判定しない）。
    /// </remarks>
    public partial class CardHistoryImportDialog : Window
    {
        private readonly ICardReader? _cardReader;

        public CardHistoryImportDialog(CardHistoryImportViewModel viewModel, ICardReader? cardReader)
        {
            InitializeComponent();

            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _cardReader = cardReader;
            DataContext = ViewModel;

            // メイン画面のカード読み取りの抑制は、ダイアログの表示範囲と一致させる（#1807。StaffAuthDialog と同じ）
            Loaded += OnLoaded;
            Closed += (s, e) => ViewModel.Cleanup();
        }

        /// <summary>このダイアログの ViewModel</summary>
        public CardHistoryImportViewModel ViewModel { get; }

        /// <summary>
        /// 取り込む対象のカードを受け取る（タッチの待ち受けは表示した後に始める）
        /// </summary>
        public void SetTarget(string cardIdm, string cardDisplayName) =>
            ViewModel.SetTarget(cardIdm, cardDisplayName);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Start();

#if DEBUG
            if (_cardReader is HybridCardReader)
            {
                DebugVirtualTouchButton.Visibility = Visibility.Visible;
            }
#endif
        }

        /// <summary>
        /// デバッグ用: 取り込む対象のカードの仮想タッチ（StaffAuthDialog の職員証の仮想タッチと同じ形。Issue #688）
        /// </summary>
        private void DebugVirtualTouchButton_Click(object sender, RoutedEventArgs e)
        {
#if DEBUG
            if (_cardReader is HybridCardReader hybridReader)
            {
                hybridReader.SimulateCardRead(ViewModel.TargetCardIdm);
            }
#endif
        }
    }
}
