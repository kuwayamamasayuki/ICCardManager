using System;
using System.Collections.Generic;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 管理者ダッシュボードの「運用状況」タブに表示するデータ（Issue #1692）
    /// </summary>
    /// <remarks>
    /// メイン画面右の警告エリアが「いま操作を妨げている事象」を出すのに対し、
    /// 本 DTO は「管理者が定期的に確認したい統制情報」をまとめる。
    /// </remarks>
    public class AdminDashboardOperationStatus
    {
        /// <summary>集計の基準日時</summary>
        public DateTime AsOf { get; set; }

        /// <summary>長期未返却と判定した日数のしきい値</summary>
        public int LongTermUnreturnedThresholdDays { get; set; }

        /// <summary>残額不足と判定した金額のしきい値</summary>
        public int WarningBalance { get; set; }

        /// <summary>帳票の出力状況を確認した年</summary>
        public int ReportYear { get; set; }

        /// <summary>帳票の出力状況を確認した月</summary>
        public int ReportMonth { get; set; }

        /// <summary>集計対象となったカードの枚数（削除済み・払戻済みを除く）</summary>
        public int TotalCardCount { get; set; }

        /// <summary>貸出中のカード枚数</summary>
        public int LentCardCount { get; set; }

        /// <summary>長期未返却（督促対象）のカード枚数</summary>
        public int LongTermUnreturnedCount { get; set; }

        /// <summary>残額がしきい値を下回っているカード枚数</summary>
        public int LowBalanceCount { get; set; }

        /// <summary>対象年月の帳票が未出力のカード枚数</summary>
        public int ReportNotExportedCount { get; set; }

        /// <summary>帳票の出力状況を判定できなかったカード枚数</summary>
        /// <remarks>
        /// 出力先フォルダが未設定・アクセス不能な場合に発生する。未出力と合算すると
        /// 「出していないのか、確認できないだけなのか」が区別できなくなるため別に数える。
        /// </remarks>
        public int ReportStatusUnknownCount { get; set; }

        /// <summary>カードごとの明細</summary>
        public IReadOnlyList<AdminDashboardCardStatus> Cards { get; set; } = new AdminDashboardCardStatus[0];
    }

    /// <summary>
    /// 管理者ダッシュボードの運用状況一覧における 1 枚のカードの状態（Issue #1692）
    /// </summary>
    public class AdminDashboardCardStatus
    {
        /// <summary>カードIDm</summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>カード種別</summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>管理番号</summary>
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>表示用のカード名（例: "はやかけん 001"）</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>貸出中かどうか</summary>
        public bool IsLent { get; set; }

        /// <summary>貸出中の場合の貸出職員名</summary>
        public string LentStaffName { get; set; } = string.Empty;

        /// <summary>貸出日時（貸出中の場合のみ）</summary>
        public DateTime? LentAt { get; set; }

        /// <summary>貸出からの経過日数（貸出中の場合のみ）</summary>
        public int? ElapsedLentDays { get; set; }

        /// <summary>長期未返却（督促対象）かどうか</summary>
        public bool IsLongTermUnreturned { get; set; }

        /// <summary>現在の残額</summary>
        public int CurrentBalance { get; set; }

        /// <summary>残額がしきい値を下回っているかどうか</summary>
        public bool IsBalanceWarning { get; set; }

        /// <summary>対象年月の帳票の出力状況</summary>
        public ReportExportState ReportState { get; set; }

        /// <summary>最終利用日</summary>
        public DateTime? LastUsageDate { get; set; }

        /// <summary>いずれかの注意事項に該当するかどうか（一覧の強調表示に使う）</summary>
        public bool HasAttention
            => IsLongTermUnreturned || IsBalanceWarning || ReportState == ReportExportState.NotExported;
    }
}
