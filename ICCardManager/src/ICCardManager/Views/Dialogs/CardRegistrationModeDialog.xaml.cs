#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ICCardManager.Services;

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

        /// <summary>
        /// 繰越額（Issue #756）。繰越モード時にユーザーが指定する月初め残高。
        /// nullの場合はカード読み取り残高を使用。
        /// </summary>
        public int? CarryoverBalance { get; set; }

        /// <summary>
        /// 繰越時点の累計受入金額（Issue #1215）。
        /// 紙の出納簿で管理していた際の、前月末時点での累計受入金額。
        /// </summary>
        public int CarryoverIncomeTotal { get; set; }

        /// <summary>
        /// 繰越時点の累計払出金額（Issue #1215）。
        /// 紙の出納簿で管理していた際の、前月末時点での累計払出金額。
        /// </summary>
        public int CarryoverExpenseTotal { get; set; }

        /// <summary>
        /// 繰越月が年度の区切り（3 月）かどうか（Issue #510 / #2326）
        /// </summary>
        /// <remarks>
        /// 3 月から繰越 = 新年度の始まりであり、導入行は「前年度より繰越」（新年度 4/1、受入＝残額）になる。
        /// 紙の出納簿の 3 月末時点の累計は**旧年度**のもので、新年度の累計は 0 から始まるため、
        /// 累計受入・累計払出は受け付けず保存もしない（<see cref="CarriesPaperCumulative"/>）。
        /// 導入行の摘要・受入の判定（<c>CardManageViewModel.BuildInitialLedgerAsync</c>）、累計を保存するかの判定、
        /// ダイアログの累計欄の有効・無効はすべてこの 1 か所に委ねる。
        /// </remarks>
        public static bool IsFiscalYearStartCarryoverMonth(int carryoverMonth) => carryoverMonth == 3;

        /// <summary>
        /// 紙の出納簿の累計受入・累計払出を当該年度の累計へ引き継ぐ登録かどうか（Issue #1215 / #2326）
        /// </summary>
        /// <remarks>
        /// 繰越モードで、繰越月が 3 月以外のときだけ真。3 月を繰越月にすると繰越年度が新年度になるため、
        /// 旧年度の紙の累計を保存すると新年度の 5 月以降の累計行に上乗せされ、物品出納簿の
        /// 「受入 − 払出 = 残額」が年度を通して成り立たなくなる（#2326）。
        /// </remarks>
        public bool CarriesPaperCumulative =>
            !IsNewPurchase
            && CarryoverMonth.HasValue
            && !IsFiscalYearStartCarryoverMonth(CarryoverMonth.Value);
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

        /// <summary>
        /// カードの現在残高（ダイアログ表示時に渡される参考値）
        /// </summary>
        private readonly int? _currentCardBalance;

        public CardRegistrationModeDialog(int? currentCardBalance = null)
        {
            _currentCardBalance = currentCardBalance;
            InitializeComponent();
            InitializeMonthComboBox();
            InitializePurchaseDatePicker();
            InitializeCarryoverBalance();
        }

        private void InitializePurchaseDatePicker()
        {
            PurchaseDatePicker.SelectedDate = DateTime.Today;
            PurchaseDatePicker.DisplayDateEnd = DateTime.Today;
        }

        private void InitializeCarryoverBalance()
        {
            if (_currentCardBalance.HasValue)
            {
                CarryoverBalanceTextBox.Text = _currentCardBalance.Value.ToString(CultureInfo.CurrentCulture);
            }
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
            UpdateCarryoverDatePreview();
            // 4 月に開くと既定の繰越月が 3 月になるため、初期表示から累計欄の有効・無効を揃える（Issue #2326）
            UpdateCarryoverCumulativeAvailability();
        }

        /// <summary>
        /// 繰越月の選択変更時（Issue #1812）
        /// </summary>
        private void CarryoverMonthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCarryoverDatePreview();
            UpdateCarryoverCumulativeAvailability();
        }

        /// <summary>
        /// 累計受入・累計払出の入力欄を、繰越月に応じて有効・無効にする（Issue #2326）
        /// </summary>
        /// <remarks>
        /// 3 月から繰越は新年度の始まり（「前年度より繰越」）で、紙の出納簿の 3 月末時点の累計は旧年度のもの。
        /// 入力を受け付けると新年度の 5 月以降の累計行に上乗せされるため、欄を無効にして理由を注記する。
        /// 判定は <see cref="CardRegistrationModeResult.IsFiscalYearStartCarryoverMonth"/> 1 か所に委ねる。
        /// </remarks>
        private void UpdateCarryoverCumulativeAvailability()
        {
            // SelectionChanged は XAML 読み込み中にも発火し得る（UpdateCarryoverDatePreview と同じ）
            if (CarryoverCumulativePanel == null || CarryoverCumulativeHintText == null)
            {
                return;
            }

            var isFiscalYearStart = CarryoverMonthCombo.SelectedValue is int carryoverMonth
                && CardRegistrationModeResult.IsFiscalYearStartCarryoverMonth(carryoverMonth);

            CarryoverCumulativePanel.IsEnabled = !isFiscalYearStart;
            CarryoverCumulativeHintText.Text = isFiscalYearStart
                ? "※ 3月から繰越は新年度の始まりとして登録するため、累計の入力は不要です（新年度の累計は0円から始まります）"
                : "※ 月次帳票の「累計」欄に加算されます（当該年度のみ）";
            AutomationProperties.SetName(CarryoverCumulativeHintText, CarryoverCumulativeHintText.Text);
        }

        /// <summary>
        /// 選択中の繰越月から生成される繰越レコード日付のプレビューを更新（Issue #1812）
        /// </summary>
        /// <remarks>
        /// 繰越月が登録月より後の場合は前年扱いになるが、これは正当な運用
        /// （2月登録で「11月から繰越」）と誤選択（2月登録で「5月から繰越」）の両方を含むため、
        /// コンボから除外せず解決結果を提示して職員に判断させる。
        /// </remarks>
        private void UpdateCarryoverDatePreview()
        {
            // SelectionChanged は XAML 読み込み中にも発火し得る。
            // 本文の TextBlock はコンボより後に生成されるため、その時点では null になる
            if (CarryoverDatePreviewText == null)
            {
                return;
            }

            var description = CarryoverMonthCombo.SelectedValue is int carryoverMonth
                ? SummaryGenerator.GetMidYearCarryoverDateDescription(carryoverMonth, DateTime.Today)
                : string.Empty;

            CarryoverDatePreviewText.Text = description;

            // AutomationProperties.Name は TextBlock の既定の読み上げ内容（Text）を上書きするため、
            // 固定文言のままにすると解決結果も注意書きも読み上げられない。本文と同じ値を都度設定する
            AutomationProperties.SetName(CarryoverDatePreviewText, description);
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
                    // Issue #1837: オーナーを渡さないと WPF は GetActiveWindow() で解決するため、
                    // アプリが非フォアグラウンドのときは ownerless になり、背後のこのダイアログが
                    // 無効化されない。以下 4 か所とも自ウィンドウが正しいオーナーなので this を渡す。
                    MessageBox.Show(
                        this,
                        "開始ページ番号は1以上の整数を入力してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    StartingPageTextBox.Focus();
                    StartingPageTextBox.SelectAll();
                    return;
                }
                result.StartingPageNumber = startingPage;

                // Issue #756: 繰越額のバリデーション
                if (!string.IsNullOrWhiteSpace(CarryoverBalanceTextBox.Text))
                {
                    if (!int.TryParse(CarryoverBalanceTextBox.Text, out var carryoverBalance) || carryoverBalance < 0)
                    {
                        MessageBox.Show(
                            this,
                            "繰越額は0以上の整数を入力してください。",
                            "入力エラー",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        CarryoverBalanceTextBox.Focus();
                        CarryoverBalanceTextBox.SelectAll();
                        return;
                    }
                    result.CarryoverBalance = carryoverBalance;
                }

                // Issue #2326: 3 月から繰越は旧年度の累計になるため受け付けない（入力欄も無効）。
                // 無効な欄に残った値で検証エラーを出さないよう、検証より前に抜ける
                if (!result.CarriesPaperCumulative)
                {
                    Result = result;
                    DialogResult = true;
                    Close();
                    return;
                }

                // Issue #1215: 累計受入金額
                if (!int.TryParse(CarryoverIncomeTotalTextBox.Text, out var carryoverIncomeTotal) || carryoverIncomeTotal < 0)
                {
                    MessageBox.Show(
                        this,
                        "累計受入は0以上の整数を入力してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    CarryoverIncomeTotalTextBox.Focus();
                    CarryoverIncomeTotalTextBox.SelectAll();
                    return;
                }
                result.CarryoverIncomeTotal = carryoverIncomeTotal;

                // Issue #1215: 累計払出金額
                if (!int.TryParse(CarryoverExpenseTotalTextBox.Text, out var carryoverExpenseTotal) || carryoverExpenseTotal < 0)
                {
                    MessageBox.Show(
                        this,
                        "累計払出は0以上の整数を入力してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    CarryoverExpenseTotalTextBox.Focus();
                    CarryoverExpenseTotalTextBox.SelectAll();
                    return;
                }
                result.CarryoverExpenseTotal = carryoverExpenseTotal;
            }

            Result = result;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// 新規購入セクションのBorderクリック時（Issue #944）
        /// </summary>
        /// <remarks>
        /// Border全体をクリック可能にすることで、無効化された購入日入力欄の
        /// クリックでも自動的に「新規購入」モードに切り替わる。
        /// </remarks>
        private void NewPurchaseBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (NewPurchaseRadio.IsChecked != true)
            {
                NewPurchaseRadio.IsChecked = true;
            }
        }

        /// <summary>
        /// 繰越セクションのBorderクリック時（Issue #944）
        /// </summary>
        /// <remarks>
        /// Border全体をクリック可能にすることで、無効化された繰越月・開始ページ・
        /// 繰越額の入力欄のクリックでも自動的に「紙の出納簿からの繰越」モードに切り替わる。
        /// </remarks>
        private void CarryoverBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (CarryoverRadio.IsChecked != true)
            {
                CarryoverRadio.IsChecked = true;
            }
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
