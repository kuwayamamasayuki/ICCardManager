using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Models
{
/// <summary>
    /// 交通系ICカードエンティティ（ic_cardテーブル）
    /// </summary>
    public class IcCard
    {
        /// <summary>
        /// ICカードIDm（主キー、16進数16文字）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// カード種別（はやかけん/nimoca/SUGOCA等）
        /// </summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>
        /// 通し番号（管理番号）
        /// </summary>
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 削除フラグ（論理削除）
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>
        /// 削除日時
        /// </summary>
        public DateTime? DeletedAt { get; set; }

        /// <summary>
        /// 払戻済フラグ（Issue #530）
        /// </summary>
        /// <remarks>
        /// 払い戻したカードは削除せず、この状態で残す。
        /// 払戻済カードは貸出対象外だが、帳票作成は可能。
        /// </remarks>
        public bool IsRefunded { get; set; }

        /// <summary>
        /// 払戻日時（Issue #530）
        /// </summary>
        public DateTime? RefundedAt { get; set; }

        /// <summary>
        /// 貸出状態（true: 貸出中）
        /// </summary>
        public bool IsLent { get; set; }

        /// <summary>
        /// 最終貸出日時
        /// </summary>
        public DateTime? LastLentAt { get; set; }

        /// <summary>
        /// 最終貸出者IDm（FK→staff）
        /// </summary>
        public string LastLentStaff { get; set; }

        /// <summary>
        /// 開始ページ番号（Issue #510: 年度途中導入対応）
        /// </summary>
        /// <remarks>
        /// 紙の出納簿からの繰越時に、帳票のページ番号をこの値から開始する。
        /// デフォルトは1。
        /// </remarks>
        public int StartingPageNumber { get; set; } = 1;

        /// <summary>
        /// 紙の出納簿からの繰越時の累計受入金額（Issue #1215）
        /// </summary>
        /// <remarks>
        /// 年度途中から本アプリに移行する場合、紙の出納簿時代の
        /// 前月末時点での累計受入金額。月次帳票の「累計」欄の初期値として使用する。
        /// <see cref="CarryoverFiscalYear"/> と同じ年度の帳票にのみ加算される。
        /// </remarks>
        public int CarryoverIncomeTotal { get; set; }

        /// <summary>
        /// 紙の出納簿からの繰越時の累計払出金額（Issue #1215）
        /// </summary>
        /// <remarks>
        /// 年度途中から本アプリに移行する場合、紙の出納簿時代の
        /// 前月末時点での累計払出金額。月次帳票の「累計」欄の初期値として使用する。
        /// <see cref="CarryoverFiscalYear"/> と同じ年度の帳票にのみ加算される。
        /// </remarks>
        public int CarryoverExpenseTotal { get; set; }

        /// <summary>
        /// 繰越累計が有効な会計年度（Issue #1215）
        /// </summary>
        /// <remarks>
        /// <see cref="CarryoverIncomeTotal"/> / <see cref="CarryoverExpenseTotal"/> が
        /// 有効な会計年度（4月始まり）。この年度の月次帳票のみ累計初期値として加算する。
        /// 翌年度以降は通常の累計計算のみが使用される。
        /// 新規購入や移行情報がない場合は null。
        /// </remarks>
        public int? CarryoverFiscalYear { get; set; }

        // === ドメインロジック ===

        /// <summary>
        /// 貸出可能な状態かどうか（未削除 かつ 未払戻 かつ 未貸出）
        /// </summary>
        public bool IsAvailableForLending => IsInOperation && !IsLent;

        /// <summary>
        /// 運用中のカードかどうか（未削除 かつ 未払戻）
        /// </summary>
        /// <remarks>
        /// 「いま業務で使われているカードの母集団」を表す。貸出中かどうかは含めない
        /// （貸出中のカードも運用中である）。
        /// 残額ダッシュボード（<c>DashboardService</c>）と管理者ダッシュボードの集計
        /// （<c>AdminDashboardService</c>）はどちらもこの母集団を使う。
        /// <para>
        /// Issue #1947: 払戻済みカードは <c>is_deleted = 0</c> のまま残るため
        /// <c>CardRepository.GetAllAsync</c> が返し続ける。残額ダッシュボードが
        /// これを除いていなかったため、払い戻しで残額 0 円になったカードが毎回
        /// 「残額 0円」の警告を出し、除去手段が無いまま本当に補充が必要なカードの
        /// 警告をスクロール外へ押し出していた。同じ判断が 2 か所に分かれていたことが
        /// 原因なので、判定はこのプロパティ 1 つに寄せる。
        /// </para>
        /// </remarks>
        public bool IsInOperation => !IsDeleted && !IsRefunded;

        /// <summary>
        /// 帳票作成可能な状態かどうか（未削除であれば払戻済でも作成可能）
        /// </summary>
        public bool CanCreateReport => !IsDeleted;

        /// <summary>
        /// 表示用のカード名（例: "はやかけん 001"）
        /// </summary>
        public string DisplayName =>
            string.IsNullOrEmpty(CardNumber) ? CardType : $"{CardType} {CardNumber}";
    }
}
