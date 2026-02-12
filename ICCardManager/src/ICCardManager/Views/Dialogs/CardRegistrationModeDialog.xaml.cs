using System;
using System.Collections.Generic;
using System.Windows;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// カード登録モードの選択結果
    /// </summary>
    public class CardRegistrationModeResult
    {
        /// <summary>
        /// 新規購入モードかどうか
        /// </summary>
        public bool IsNewPurchase { get; set; } = true;

        /// <summary>
        /// 繰越元の月（1-12）。新規購入モードの場合はnull
        /// </summary>
        public int? CarryoverMonth { get; set; }

        /// <summary>
        /// 開始ページ番号。デフォルトは1
        /// </summary>
        public int StartingPageNumber { get; set; } = 1;

        /// <summary>
        /// 購入日（Issue #658）。新規購入モード時に指定可能。nullの場合は当日
        /// </summary>
        public DateTime? PurchaseDate { get; set; }
    }

    /// <summary>
    /// カード登録モード選択ダイアログ（Issue #510）
    /// </summary>
    /// <remarks>
    /// 年度途中から本アプリを導入する場合に、新規購入か紙の出納簿からの繰越かを選択させる。
    /// 繰越の場合は繰越月と開始ページ番号を指定できる。
    /// </remarks>
    public partial class CardRegistrationModeDialog : Window
    {
        /// <summary>
        /// 選択結果
        /// </summary>
        public CardRegistrationModeResult? Result { get; private set; }

        /// <summary>
        /// 会計年度順の月リスト（4月〜3月）
        /// </summary>
        private readonly List<MonthItem> _fiscalYearMonths = new List<MonthItem>
        {
            new MonthItem(4, "4月"),
            new MonthItem(5, "5月"),
            new MonthItem(6, "6月"),
            new MonthItem(7, "7月"),
            new MonthItem(8, "8月"),
            new MonthItem(9, "9月"),
            new MonthItem(10, "10月"),
            new MonthItem(11, "11月"),
            new MonthItem(12, "12月"),
            new MonthItem(1, "1月"),
            new MonthItem(2, "2月"),
            new MonthItem(3, "3月")
        };

        public CardRegistrationModeDialog()
        {
            InitializeComponent();
            InitializeMonthComboBox();
            InitializePurchaseDatePicker();
        }

        private void InitializePurchaseDatePicker()
        {
            PurchaseDatePicker.SelectedDate = DateTime.Today;
            PurchaseDatePicker.DisplayDateEnd = DateTime.Today;
        }

        private void InitializeMonthComboBox()
        {
            CarryoverMonthCombo.ItemsSource = _fiscalYearMonths;
            CarryoverMonthCombo.DisplayMemberPath = "DisplayName";
            CarryoverMonthCombo.SelectedValuePath = "Month";
            // デフォルトは前月（現在の月の1つ前）
            var currentMonth = System.DateTime.Now.Month;
            var previousMonth = currentMonth == 1 ? 12 : currentMonth - 1;
            CarryoverMonthCombo.SelectedValue = previousMonth;
        }

        private void NewPurchaseRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (CarryoverOptionsPanel != null)
            {
                CarryoverOptionsPanel.IsEnabled = false;
            }
            if (NewPurchaseOptionsPanel != null)
            {
                NewPurchaseOptionsPanel.IsEnabled = true;
            }
        }

        private void CarryoverRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (CarryoverOptionsPanel != null)
            {
                CarryoverOptionsPanel.IsEnabled = true;
            }
            if (NewPurchaseOptionsPanel != null)
            {
                NewPurchaseOptionsPanel.IsEnabled = false;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var result = new CardRegistrationModeResult();

            if (NewPurchaseRadio.IsChecked == true)
            {
                result.IsNewPurchase = true;
                result.CarryoverMonth = null;
                result.StartingPageNumber = 1;
                result.PurchaseDate = PurchaseDatePicker.SelectedDate ?? DateTime.Today;
            }
            else
            {
                result.IsNewPurchase = false;
                result.CarryoverMonth = (int?)CarryoverMonthCombo.SelectedValue;

                // 開始ページ番号のバリデーション
                if (!int.TryParse(StartingPageTextBox.Text, out var startingPage) || startingPage < 1)
                {
                    MessageBox.Show(
                        "開始ページ番号は1以上の整数を入力してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    StartingPageTextBox.Focus();
                    StartingPageTextBox.SelectAll();
                    return;
                }
                result.StartingPageNumber = startingPage;
            }

            Result = result;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 月選択用の内部クラス
        /// </summary>
        private class MonthItem
        {
            public int Month { get; }
            public string DisplayName { get; }

            public MonthItem(int month, string displayName)
            {
                Month = month;
                DisplayName = displayName;
            }
        }
    }
}
