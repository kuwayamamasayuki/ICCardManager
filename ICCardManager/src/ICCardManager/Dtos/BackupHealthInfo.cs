using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// バックアップの健全性情報（Issue #1689）
    /// </summary>
    /// <remarks>
    /// 「バックアップが正しく動き続けているか」を管理者が確認するための情報を集約した DTO。
    /// システム管理画面（F6）の「バックアップ状況」セクションと、
    /// 起動時の「バックアップが長期間成功していない」警告の両方で使用する。
    /// </remarks>
    public class BackupHealthInfo
    {
        /// <summary>
        /// 最後にバックアップが成功した日時。一度も記録がない場合は null
        /// </summary>
        /// <remarks>
        /// null は「失敗している」ではなく「判断材料がない」を意味する。
        /// Issue #1689 導入前から使っている既存環境では初回起動時点で必ず null になるため、
        /// 警告判定では null を警告なしとして扱うこと。
        /// </remarks>
        public DateTime? LastSuccessAt { get; set; }

        /// <summary>
        /// 最後にバックアップを実施した PC 名。共有モードでどの PC が実施したかの追跡に使用
        /// </summary>
        public string LastSuccessMachineName { get; set; }

        /// <summary>
        /// バックアップ保存先に現存する世代（バックアップファイル）の数
        /// </summary>
        public int GenerationCount { get; set; }

        /// <summary>
        /// 自動バックアップの保持日数（Issue #1813。バックアップのある日を新しい順にこの日数だけ残す）
        /// </summary>
        public int RetentionDays { get; set; }

        /// <summary>
        /// 手動バックアップ・リストア前バックアップの保持件数の上限（Issue #1813）
        /// </summary>
        public int MaxManualGenerations { get; set; }

        /// <summary>
        /// バックアップ保存先フォルダのパス（実際に使用される正規化済みパス）
        /// </summary>
        public string BackupFolderPath { get; set; }

        /// <summary>
        /// バックアップ保存先の空きディスク容量（バイト）。取得できない場合は null（＝不明）
        /// </summary>
        public long? FreeSpaceBytes { get; set; }

        /// <summary>
        /// 最後に VACUUM を実行した日付。未実行の場合は null
        /// </summary>
        public DateTime? LastVacuumDate { get; set; }

        /// <summary>
        /// 最後に VACUUM を実行した PC 名
        /// </summary>
        public string LastVacuumMachineName { get; set; }

        /// <summary>
        /// 共有フォルダモードで動作しているか（PC 名の表示要否の判定に使用）
        /// </summary>
        public bool IsSharedMode { get; set; }

        /// <summary>
        /// 最終成功からの経過日数を返す。記録がない場合は null
        /// </summary>
        /// <param name="now">現在日時（テスト容易性のため引数で受け取る）</param>
        public int? GetDaysSinceLastSuccess(DateTime now)
        {
            if (LastSuccessAt == null)
                return null;

            // 「日付単位」で数える。同日中の実行は 0 日、前日の実行は 1 日となる。
            var days = (int)(now.Date - LastSuccessAt.Value.Date).TotalDays;
            return days < 0 ? 0 : days;
        }
    }
}
