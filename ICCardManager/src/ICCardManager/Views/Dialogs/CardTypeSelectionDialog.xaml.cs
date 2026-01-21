using System.Windows;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// カード種別選択の結果
    /// </summary>
    public enum CardTypeSelectionResult
    {
        /// <summary>キャンセル</summary>
        Cancel,
        /// <summary>職員証として登録</summary>
        StaffCard,
        /// <summary>交通系ICカードとして登録</summary>
        IcCard
    }

    /// <summary>
    /// 未登録カードの種別選択ダイアログ
    /// </summary>
    /// <remarks>
    /// Issue #312: IDmからカード種別を自動判別することは技術的に不可能なため、
    /// ユーザーに職員証か交通系ICカードかを選択させる。
    /// </remarks>
    public partial class CardTypeSelectionDialog : Window
    {
        /// <summary>
        /// 選択結果
        /// </summary>
        public CardTypeSelectionResult SelectionResult { get; private set; } = CardTypeSelectionResult.Cancel;

        public CardTypeSelectionDialog()
        {
            InitializeComponent();
        }

        private void StaffCardButton_Click(object sender, RoutedEventArgs e)
        {
            SelectionResult = CardTypeSelectionResult.StaffCard;
            DialogResult = true;
            Close();
        }

        private void IcCardButton_Click(object sender, RoutedEventArgs e)
        {
            SelectionResult = CardTypeSelectionResult.IcCard;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectionResult = CardTypeSelectionResult.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
