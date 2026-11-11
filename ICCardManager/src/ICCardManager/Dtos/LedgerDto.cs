#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
namespace ICCardManager.Dtos
{
    /// <summary>
    /// 利用履歴DTO
    /// ViewModelで使用する履歴表示用オブジェクト
    /// </summary>
    public class LedgerDto : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 統合対象として選択されているか（チェックボックス用）
        /// </summary>
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        /// <summary>
        /// レコードID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// カードIDm
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// 出納日付
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 表示用: 日付（和暦変換済み）
        /// </summary>
        public string DateDisplay { get; set; } = string.Empty;

        /// <summary>
        /// 摘要
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// 受入金額（チャージ額）
        /// </summary>
        public int Income { get; set; }

        /// <summary>
        /// 払出金額（利用額）
        /// </summary>
        public int Expense { get; set; }

        /// <summary>
        /// 残額
        /// </summary>
        public int Balance { get; set; }

        /// <summary>
        /// 利用者氏名（DB の staff_name そのもの。「外N名」は含まない）
        /// </summary>
        public string? StaffName { get; set; }

        /// <summary>
        /// 同行者数（本人を含まない。Issue #1906）
        /// </summary>
        public int CompanionCount { get; set; }

        /// <summary>
        /// 一覧表示用の氏名（同行者がいれば「氏名 外N名」、Issue #1906）
        /// </summary>
        public string DisplayStaffName => Common.StaffNameFormatter.Format(StaffName, CompanionCount);

        /// <summary>
        /// 備考
        /// </summary>
        public string? Note { get; set; }

        /// <summary>
        /// 貸出中レコードフラグ
        /// </summary>
        public bool IsLentRecord { get; set; }

        /// <summary>
        /// 導入行の種別（Issue #2371。導入行でなければ <c>null</c>）。<see cref="Models.Ledger.InitialRecordKind"/> の写し。
        /// 履歴の画面は導入行かどうかを摘要ではなくこの値で判定する。合成した繰越行（<see cref="IsCarryoverRow"/>）は導入行ではない。
        /// </summary>
        public Models.InitialRecordKind? InitialRecordKind { get; set; }

        /// <summary>
        /// 導入行（カード登録時に書く行）かどうか（Issue #2371）
        /// </summary>
        public bool IsInitialRecord => InitialRecordKind.HasValue;

        /// <summary>
        /// 直前の返却で記録された行かどうか（Issue #1907: 返却確認のハイライト用）
        /// </summary>
        /// <remarks>
        /// 返却直後にメイン画面へ自動表示した履歴で、今回の返却で台帳に入った行を職員が
        /// 見分けられるようにする。一覧を作り直すたびに <c>MainViewModel</c> が付け直す。
        /// 色だけに頼らないよう <see cref="RecentlyRecordedMark"/> の文字も併せて表示する（4 要素原則）。
        /// </remarks>
        public bool IsRecentlyRecorded
        {
            get => _isRecentlyRecorded;
            set
            {
                if (_isRecentlyRecorded != value)
                {
                    _isRecentlyRecorded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecentlyRecorded)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecentlyRecordedMark)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryToolTip)));
                }
            }
        }

        private bool _isRecentlyRecorded;

        /// <summary>
        /// <see cref="IsRecentlyRecorded"/> の行の ToolTip（行全体と、摘要のセルの <see cref="SummaryToolTip"/> で共有する）
        /// </summary>
        public const string RecentlyRecordedToolTip = "今回の返却で記録された行";

        /// <summary>
        /// 「今回」列に表示する印（<see cref="IsRecentlyRecorded"/> のとき「✔」、それ以外は空）
        /// </summary>
        public string RecentlyRecordedMark => IsRecentlyRecorded ? "✔" : string.Empty;

        /// <summary>
        /// 繰越行フラグ（前年度繰越・前月繰越など、合成的に表示する行）
        /// </summary>
        public bool IsCarryoverRow { get; set; }

        /// <summary>
        /// 残高不整合フラグ（Issue #1052: 警告クリック時のハイライト表示用）
        /// </summary>
        private bool _hasBalanceInconsistency;
        public bool HasBalanceInconsistency
        {
            get => _hasBalanceInconsistency;
            set
            {
                if (_hasBalanceInconsistency != value)
                {
                    _hasBalanceInconsistency = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBalanceInconsistency)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryToolTip)));
                }
            }
        }

        /// <summary>
        /// 残高不整合時のツールチップメッセージ（Issue #1052）
        /// </summary>
        public string BalanceInconsistencyMessage
        {
            get => _balanceInconsistencyMessage;
            set
            {
                var normalized = value ?? string.Empty;
                if (_balanceInconsistencyMessage != normalized)
                {
                    _balanceInconsistencyMessage = normalized;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BalanceInconsistencyMessage)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryToolTip)));
                }
            }
        }

        private string _balanceInconsistencyMessage = string.Empty;

        /// <summary>
        /// 摘要のセルの ToolTip（Issue #2424）。切り詰めた摘要の全文に、行の ToolTip の文言を添える
        /// </summary>
        /// <remarks>
        /// <para>
        /// 摘要は残りの幅を取る最も広い列で、切り詰めた全文を読めるようセルに ToolTip を置く（#2076 の利用者の列と同じ形）。
        /// <c>ToolTipService</c> は最も近い祖先の ToolTip を使うため、摘要の全文だけを出すと、行の ToolTip
        /// （#1052 の残高不整合の説明・#1907 の「今回の返却で記録された行」）が行の大部分で摘要に置き換わる。
        /// 行の文言を先頭に、摘要の全文を後ろに並べて両方を読めるようにする。優先順位は行のスタイルと同じ
        /// （残高不整合が今回の返却より優先）。
        /// </para>
        /// <para>
        /// 「切り詰めたときだけ付ける」形は採らない。切り詰めは描画の幅で決まり、長い摘要の行こそ行の文言が隠れる。
        /// 行の文言も摘要も無いときは null（中身の無いツールチップを出さない）。
        /// </para>
        /// </remarks>
        public string? SummaryToolTip
        {
            get
            {
                var rowMessage = HasBalanceInconsistency && !string.IsNullOrEmpty(BalanceInconsistencyMessage)
                    ? BalanceInconsistencyMessage
                    : IsRecentlyRecorded ? RecentlyRecordedToolTip : null;
                var summary = string.IsNullOrEmpty(Summary) ? null : Summary;

                if (rowMessage == null)
                {
                    return summary;
                }

                return summary == null ? rowMessage : $"{rowMessage}\n摘要: {summary}";
            }
        }

        /// <summary>
        /// 利用履歴詳細
        /// </summary>
        public List<LedgerDetailDto> Details { get; set; } = new();

        #region 表示用プロパティ

        /// <summary>
        /// 表示用: 受入金額（金額がある場合のみ表示）
        /// </summary>
        public string IncomeDisplay => DisplayFormatters.FormatAmountOrEmpty(Income);

        /// <summary>
        /// 表示用: 払出金額（金額がある場合のみ表示）
        /// </summary>
        public string ExpenseDisplay => DisplayFormatters.FormatAmountOrEmpty(Expense);

        /// <summary>
        /// 表示用: 残額
        /// </summary>
        public string BalanceDisplay => DisplayFormatters.FormatBalance(Balance);

        /// <summary>
        /// 受入金額があるかどうか
        /// </summary>
        public bool HasIncome => Income > 0;

        /// <summary>
        /// 払出金額があるかどうか
        /// </summary>
        public bool HasExpense => Expense > 0;

        /// <summary>
        /// 詳細件数（直接設定用）
        /// </summary>
        public int DetailCountValue { get; set; }

        /// <summary>
        /// 詳細があるかどうか（詳細が2件以上ある場合）
        /// </summary>
        public bool HasDetails => DetailCount > 1;

        /// <summary>
        /// 詳細件数
        /// </summary>
        public int DetailCount => Details?.Count > 0 ? Details.Count : DetailCountValue;

        #endregion
    }
}
