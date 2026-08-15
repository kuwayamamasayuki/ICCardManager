using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Services;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 繰越情報消失一覧ダイアログの ViewModel（Issue #1758）
    /// </summary>
    /// <remarks>
    /// 表示専用。復旧操作は持たない（Issue #1758 の案A）。
    /// 唯一の役割は「失われた元の値を、復旧を依頼する相手へ正確に伝えられる形で見せる」こと。
    /// </remarks>
    public partial class CarryoverDataLossViewModel : ObservableObject
    {
        /// <summary>
        /// その項目は失われていないことを示す表示文字列
        /// </summary>
        /// <remarks>
        /// 空欄にすると「値が 0 だった」とも読めてしまうため、明示的な文言を置く。
        /// </remarks>
        public const string NotLostText = "（消失なし）";

        /// <summary>被害が1件も無かったときの案内</summary>
        public const string NoLossMessage = "繰越情報が失われたカードはありません。";

        /// <summary>
        /// 検出そのものに失敗したときの案内
        /// </summary>
        /// <remarks>
        /// 一覧が空になる理由は「被害が無い」と「確認できなかった」の2つある。
        /// どちらにも <see cref="NoLossMessage"/> を出すと、DB 接続断で確認できなかっただけの
        /// 利用者に「うちは無事だ」と誤って結論させる。**「判定できない」を「異常なし」に丸めない**
        /// （.claude/rules/development-conventions.md の Issue #1748 の項と同じ判断）。
        /// </remarks>
        public const string DetectionFailedMessage =
            "繰越情報の確認に失敗しました（被害が無いという意味ではありません）。" +
            "データベースへの接続状態を確認したうえで、もう一度この画面を開いてください。";

        private readonly ICarryoverDataLossDetector _detector;

        public CarryoverDataLossViewModel(ICarryoverDataLossDetector detector)
        {
            _detector = detector;
        }

        /// <summary>繰越情報を失ったカードの一覧</summary>
        public ObservableCollection<CarryoverDataLossRow> Items { get; } = new ObservableCollection<CarryoverDataLossRow>();

        /// <summary>被害が1件以上あるか（0件のときの案内表示を切り替える）</summary>
        [ObservableProperty]
        private bool _hasItems;

        /// <summary>
        /// 一覧が空のときに表示する案内
        /// </summary>
        /// <remarks>
        /// 「被害が無い」と「確認できなかった」を同じ文言で表さないための state。
        /// 表示条件（<see cref="HasItems"/>）と文言を分けることで、View 側は単一の
        /// <c>DataTrigger</c> のままで両者を出し分けられる。
        /// </remarks>
        [ObservableProperty]
        private string _emptyStateMessage = NoLossMessage;

        /// <summary>
        /// 検出をやり直して一覧を作り直す
        /// </summary>
        /// <remarks>
        /// <para>
        /// 復旧の進み具合を確認するために再実行できる。全消ししてから詰め直すのは
        /// 「自分が出した行だけを入れ替える」形（本一覧の行はすべてこのメソッドが作る）。
        /// </para>
        /// <para>
        /// 失敗しても例外は握りつぶさず、<see cref="EmptyStateMessage"/> を切り替えてから
        /// そのまま伝える。呼び出し元（ダイアログ）がエラー通知を出す責務を持つため。
        /// 成功時に文言を戻すのも必須で、戻さないと復旧後の再読み込みで今度は逆に
        /// 「まだ確認できていない」と誤解させる。
        /// </para>
        /// </remarks>
        public async Task InitializeAsync()
        {
            IReadOnlyList<CarryoverDataLossItem> items;
            try
            {
                items = await _detector.DetectAsync();
            }
            catch
            {
                Items.Clear();
                HasItems = false;
                EmptyStateMessage = DetectionFailedMessage;
                throw;
            }

            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(CarryoverDataLossRow.From(item));
            }

            HasItems = Items.Count > 0;
            EmptyStateMessage = NoLossMessage;
        }
    }

    /// <summary>
    /// 繰越情報消失一覧の1行（Issue #1758）
    /// </summary>
    public class CarryoverDataLossRow
    {
        /// <summary>カード名（例: "はやかけん 001"）</summary>
        public string CardDisplayName { get; set; }

        /// <summary>失われた開始ページ番号</summary>
        public string LostStartingPageNumberText { get; set; }

        /// <summary>失われた繰越累計受入金額</summary>
        public string LostCarryoverIncomeTotalText { get; set; }

        /// <summary>失われた繰越累計払出金額</summary>
        public string LostCarryoverExpenseTotalText { get; set; }

        /// <summary>失われた繰越累計の対象年度</summary>
        public string LostCarryoverFiscalYearText { get; set; }

        /// <summary>値が失われた操作の日時</summary>
        public string LostAtText { get; set; }

        /// <summary>値を失わせた操作の操作者名</summary>
        public string OperatorName { get; set; }

        public static CarryoverDataLossRow From(CarryoverDataLossItem item) => new CarryoverDataLossRow
        {
            CardDisplayName = item.CardDisplayName,
            LostStartingPageNumberText = item.LostStartingPageNumber?.ToString() ?? CarryoverDataLossViewModel.NotLostText,
            LostCarryoverIncomeTotalText = FormatAmount(item.LostCarryoverIncomeTotal),
            LostCarryoverExpenseTotalText = FormatAmount(item.LostCarryoverExpenseTotal),

            // 年度は DB 列 carryover_fiscal_year の生値（西暦）で示す。復旧はこの値を DB へ書き戻す
            // 作業になるため、和暦へ変換すると依頼を受けた側が戻し算をすることになる。
            LostCarryoverFiscalYearText = item.LostCarryoverFiscalYear == null
                ? CarryoverDataLossViewModel.NotLostText
                : $"{item.LostCarryoverFiscalYear}年度",

            LostAtText = DisplayFormatters.FormatDateTime(item.LostAt),
            OperatorName = item.OperatorName
        };

        private static string FormatAmount(int? value) =>
            value == null ? CarryoverDataLossViewModel.NotLostText : DisplayFormatters.FormatAmountWithUnit(value);
    }
}
