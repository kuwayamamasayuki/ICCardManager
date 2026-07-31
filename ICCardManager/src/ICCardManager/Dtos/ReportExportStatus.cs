using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 帳票（物品出納簿）の出力状況（Issue #1691）
    /// </summary>
    /// <remarks>
    /// 判定は出力先フォルダの実ファイルを見る。DB に出力履歴を持たないため、
    /// 出力後にファイルを消せば「未出力」に戻る（表示と手元のファイルが必ず一致する）。
    /// </remarks>
    public enum ReportExportState
    {
        /// <summary>
        /// 対象年月の帳票がまだ出力されていない
        /// </summary>
        NotExported = 0,

        /// <summary>
        /// 対象年月の帳票が出力済み（年度ファイルに対象月のシートが存在する）
        /// </summary>
        Exported = 1,

        /// <summary>
        /// 判定できない（出力先フォルダが未指定/存在しない、ファイルが壊れている等）
        /// </summary>
        Unknown = 2,
    }

    /// <summary>
    /// 出力状況を調べる対象カード（Issue #1691）
    /// </summary>
    /// <remarks>
    /// 年度ファイル名はカード種別と管理番号から決まるため、IDm だけでは判定できない。
    /// </remarks>
    public class ReportExportTarget
    {
        /// <summary>
        /// カードIDm
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// カード種別（ファイル名の構成要素）
        /// </summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>
        /// 管理番号（ファイル名の構成要素）
        /// </summary>
        public string CardNumber { get; set; } = string.Empty;
    }

    /// <summary>
    /// カード1枚ぶんの帳票出力状況（Issue #1691）
    /// </summary>
    public class ReportExportStatus
    {
        /// <summary>
        /// カードIDm
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// 出力状況
        /// </summary>
        public ReportExportState State { get; set; }

        /// <summary>
        /// 判定に用いた年度ファイルのフルパス（出力先フォルダが未確定の場合は空）
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 年度ファイルの最終更新日時（出力済みの場合のみ設定）
        /// </summary>
        /// <remarks>
        /// 年度ファイルは月ごとのシートを追記していく形式のため、この日時は
        /// 「対象月を出力した日時」ではなく「そのカードの年度ファイルを最後に更新した日時」を表す。
        /// </remarks>
        public DateTime? LastWriteTime { get; set; }
    }
}
