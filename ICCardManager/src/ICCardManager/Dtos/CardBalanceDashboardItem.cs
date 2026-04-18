using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
namespace ICCardManager.Dtos
{
/// <summary>
    /// カード残高ダッシュボード表示用DTO
    /// メイン画面でカードの残高状況を一覧表示するために使用
    /// </summary>
    public class CardBalanceDashboardItem
    {
        /// <summary>
        /// カードIDm（16進数16文字）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// カード種別（はやかけん/nimoca/SUGOCA等）
        /// </summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>
        /// 管理番号
        /// </summary>
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>
        /// 現在残高（円）
        /// </summary>
        public int CurrentBalance { get; set; }

        /// <summary>
        /// 残高警告フラグ（残高が閾値以下の場合true）
        /// </summary>
        public bool IsBalanceWarning { get; set; }

        /// <summary>
        /// 最終利用日
        /// </summary>
        public DateTime? LastUsageDate { get; set; }

        /// <summary>
        /// 貸出状態（true: 貸出中）
        /// </summary>
        public bool IsLent { get; set; }

        /// <summary>
        /// 貸出者名（貸出中の場合）
        /// </summary>
        public string LentStaffName { get; set; }

        #region 表示用プロパティ

        /// <summary>
        /// 表示用: カード名（種別 + 番号）
        /// </summary>
        public string DisplayName => $"{CardType} {CardNumber}";

        /// <summary>
        /// 表示用: 残高（円単位、3桁区切り）
        /// </summary>
        public string BalanceDisplay => DisplayFormatters.FormatBalanceWithYenPrefix(CurrentBalance);

        /// <summary>
        /// 表示用: 警告アイコン（⚠）
        /// </summary>
        public string WarningIcon => IsBalanceWarning ? "⚠" : "";

        /// <summary>
        /// 表示用: 貸出状態アイコン
        /// </summary>
        /// <remarks>
        /// Issue #1274: <see cref="LendingStatusPresenter"/> で一元管理。
        /// </remarks>
        public string LentStatusIcon => LendingStatusPresenter.Resolve(IsLent, isRefunded: false).Icon;

        /// <summary>
        /// 表示用: 貸出状態テキスト（貸出者名なしの短いラベル）
        /// </summary>
        /// <remarks>
        /// 貸出者名を含むバージョンは <see cref="LentInfoDisplay"/> を使用。
        /// </remarks>
        public string LentStatusDisplay => LendingStatusPresenter.Resolve(IsLent, isRefunded: false).ShortText;

        /// <summary>
        /// 表示用: 貸出情報（貸出中の場合は貸出者名を表示）
        /// </summary>
        public string LentInfoDisplay => LendingStatusPresenter.Resolve(IsLent, isRefunded: false, LentStaffName).ShortText;

        /// <summary>
        /// Issue #1274: アクセシビリティ用の完全な説明文。
        /// スクリーンリーダーでの状態読み上げに使用する
        /// （<c>AutomationProperties.Name</c> へのバインド対象）。
        /// </summary>
        public string LentStatusAccessibilityText =>
            LendingStatusPresenter.Resolve(IsLent, isRefunded: false, LentStaffName).AccessibilityText;

        /// <summary>
        /// 表示用: 最終利用日
        /// </summary>
        public string LastUsageDateDisplay => DisplayFormatters.FormatDate(LastUsageDate);

        /// <summary>
        /// 表示用: 行の背景色（警告時は薄い赤）
        /// </summary>
        public string RowBackgroundColor => IsBalanceWarning ? "#FFEBEE" : "Transparent";

        #endregion
    }
}
