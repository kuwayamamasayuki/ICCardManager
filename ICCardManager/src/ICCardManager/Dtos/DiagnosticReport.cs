using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Common;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 接続診断における 1 項目の判定結果（Issue #1690）
    /// </summary>
    public enum DiagnosticStatus
    {
        /// <summary>
        /// 正常。期待どおり動作している
        /// </summary>
        Ok,

        /// <summary>
        /// 警告。今すぐ使えなくなるわけではないが、放置すると問題になる
        /// </summary>
        Warning,

        /// <summary>
        /// 異常。該当機能が使えない
        /// </summary>
        Error,

        /// <summary>
        /// この環境では診断対象外（例: ローカルモードにおける共有フォルダ接続状態）
        /// </summary>
        /// <remarks>
        /// 「判定していない」ことを明示する値であり、正常でも異常でもない。
        /// <see cref="DiagnosticReport.OverallStatus"/> の集約からは除外される。
        /// </remarks>
        NotApplicable
    }

    /// <summary>
    /// 接続診断の項目種別（Issue #1690）
    /// </summary>
    /// <remarks>
    /// 宣言順がそのまま画面・コピー結果での表示順になる。
    /// 「アプリの中心（DB）から周辺（リーダー・保存先）へ」の順で並べ、
    /// 障害切り分けの思考順と一致させている。
    /// </remarks>
    public enum DiagnosticItemKind
    {
        /// <summary>データベースファイルへ到達できるか</summary>
        DatabaseReachability,

        /// <summary>データベースへ書き込めるか</summary>
        DatabaseWritable,

        /// <summary>ジャーナルモードがクラッシュ耐性の高い DELETE のままか</summary>
        JournalMode,

        /// <summary>共有フォルダモードにおける直近のヘルスチェック結果</summary>
        SharedFolderConnection,

        /// <summary>ICカードリーダー（PaSoRi）の接続状態</summary>
        CardReader,

        /// <summary>バックアップ保存先へ書き込めるか</summary>
        BackupFolderWritable,

        /// <summary>バックアップ保存先の空きディスク容量</summary>
        DiskFreeSpace,

        /// <summary>バックアップが直近で成功しているか</summary>
        BackupHealth
    }

    /// <summary>
    /// 接続診断の 1 項目分の結果（Issue #1690）
    /// </summary>
    public class DiagnosticItem
    {
        /// <summary>
        /// 項目種別
        /// </summary>
        public DiagnosticItemKind Kind { get; set; }

        /// <summary>
        /// 項目名（一覧の左列に表示。例: 「データベース到達性」）
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 判定結果
        /// </summary>
        public DiagnosticStatus Status { get; set; }

        /// <summary>
        /// 一覧に表示する 1 行要約（例: 「接続できます」）
        /// </summary>
        /// <remarks>
        /// 列幅の制約があるため簡潔でよい。3 要素メッセージは <see cref="DetailText"/> が担う
        /// （表示領域が制約された箇所に最小文字数基準を適用しない方針。Issue #1688）。
        /// </remarks>
        public string SummaryText { get; set; } = string.Empty;

        /// <summary>
        /// 詳細ペインとコピー結果に表示する説明
        /// </summary>
        /// <remarks>
        /// <see cref="DiagnosticStatus.Warning"/> / <see cref="DiagnosticStatus.Error"/> の場合は
        /// 「何が・なぜ・どうすれば」の 3 要素を満たし、行動指示で終わること
        /// （<c>.claude/rules/error-messages.md</c>）。
        /// 正常・対象外の場合は状態の説明のみでよい。
        /// </remarks>
        public string DetailText { get; set; } = string.Empty;

        /// <summary>
        /// 利用者の対処が必要な項目（警告または異常）かどうか
        /// </summary>
        public bool IsProblem =>
            Status == DiagnosticStatus.Warning || Status == DiagnosticStatus.Error;

        /// <summary>
        /// 判定を表すアイコン（色のみに依存しない状態伝達のため）
        /// </summary>
        public string StatusIcon => DiagnosticStatusPresenter.GetIcon(Status);

        /// <summary>
        /// 判定を表す日本語ラベル（例: 「正常」）
        /// </summary>
        public string StatusLabel => DiagnosticStatusPresenter.GetLabel(Status);

        /// <summary>
        /// 判定に対応する文字色のリソースキー名
        /// </summary>
        /// <remarks>
        /// 色値リテラルではなくリソースキー名を返し、XAML 側で
        /// <c>ResourceKeyToBrushConverter</c> 経由でブラシ解決する（Issue #1392、#1461）。
        /// </remarks>
        public string StatusForegroundResourceKey =>
            DiagnosticStatusPresenter.GetForegroundResourceKey(Status);
    }

    /// <summary>
    /// 接続診断の全体結果（Issue #1690）
    /// </summary>
    /// <remarks>
    /// インターネット非接続の官公庁環境では IT 担当が物理的に遠く、障害の切り分けが電話越しになる。
    /// 本 DTO は「アプリが依存する外部リソースの状態」と「その PC の環境情報」をひとまとめにし、
    /// クリップボード経由でそのまま IT 担当へ共有できる形を目指す。
    /// </remarks>
    public class DiagnosticReport
    {
        /// <summary>
        /// 診断を実行した日時
        /// </summary>
        public DateTime DiagnosedAt { get; set; }

        /// <summary>
        /// 各診断項目の結果（<see cref="DiagnosticItemKind"/> の宣言順）
        /// </summary>
        public IReadOnlyList<DiagnosticItem> Items { get; set; } = new List<DiagnosticItem>();

        /// <summary>
        /// アプリケーションのバージョン
        /// </summary>
        public string AppVersion { get; set; } = string.Empty;

        /// <summary>
        /// 診断を実行した PC 名
        /// </summary>
        public string MachineName { get; set; } = string.Empty;

        /// <summary>
        /// OS のバージョン情報
        /// </summary>
        public string OsDescription { get; set; } = string.Empty;

        /// <summary>
        /// 使用中のデータベースファイルのパス
        /// </summary>
        public string DatabasePath { get; set; } = string.Empty;

        /// <summary>
        /// 共有フォルダモードで動作しているか
        /// </summary>
        public bool IsSharedMode { get; set; }

        /// <summary>
        /// 全項目を集約した総合判定
        /// </summary>
        /// <remarks>
        /// 最も重い判定を採用する（異常 &gt; 警告 &gt; 正常）。
        /// <see cref="DiagnosticStatus.NotApplicable"/> は「判定していない」を意味するため集約に含めない。
        /// 全項目が対象外、または項目が 1 件もない場合のみ <see cref="DiagnosticStatus.NotApplicable"/> になる。
        /// </remarks>
        public DiagnosticStatus OverallStatus
        {
            get
            {
                var items = Items;
                if (items == null || items.Count == 0)
                    return DiagnosticStatus.NotApplicable;

                if (items.Any(i => i != null && i.Status == DiagnosticStatus.Error))
                    return DiagnosticStatus.Error;

                if (items.Any(i => i != null && i.Status == DiagnosticStatus.Warning))
                    return DiagnosticStatus.Warning;

                if (items.Any(i => i != null && i.Status == DiagnosticStatus.Ok))
                    return DiagnosticStatus.Ok;

                return DiagnosticStatus.NotApplicable;
            }
        }

        /// <summary>
        /// 対処が必要な項目（警告・異常）の件数
        /// </summary>
        public int ProblemCount => Items?.Count(i => i != null && i.IsProblem) ?? 0;
    }
}
