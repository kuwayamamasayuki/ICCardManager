using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Services;
namespace ICCardManager.Models
{
    /// <summary>
    /// 利用履歴概要エンティティ（ledgerテーブル）
    /// 物品出納簿の1行に対応
    /// </summary>
    public class Ledger
    {
        /// <summary>
        /// レコードID（主キー、自動採番）
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 交通系ICカードIDm（FK→ic_card）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// 貸出者IDm（FK→staff）
        /// </summary>
        public string LenderIdm { get; set; }

        /// <summary>
        /// 出納年月日
        /// </summary>
        public DateTime Date { get; set; }

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
        /// 利用者氏名（スナップショット保存）
        /// </summary>
        public string StaffName { get; set; }

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 同行者数（本人を含まない。既定 0）
        /// </summary>
        /// <remarks>
        /// Issue #1906: 複数名で同一交通系ICカードを利用した場合の人数。
        /// 氏名欄の「外N名」は <see cref="DisplayStaffName"/> が導出し、<see cref="StaffName"/> には書き込まない。
        /// </remarks>
        public int CompanionCount { get; set; }

        /// <summary>
        /// 表示用の氏名（同行者がいれば「氏名 外N名」、Issue #1906）
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayStaffName => Common.StaffNameFormatter.Format(StaffName, CompanionCount);

        /// <summary>
        /// 返却者IDm（FK→staff）
        /// </summary>
        public string ReturnerIdm { get; set; }

        /// <summary>
        /// 貸出日時
        /// </summary>
        public DateTime? LentAt { get; set; }

        /// <summary>
        /// 返却日時
        /// </summary>
        public DateTime? ReturnedAt { get; set; }

        /// <summary>
        /// 貸出中レコードフラグ（true: 貸出中）
        /// </summary>
        public bool IsLentRecord { get; set; }

        /// <summary>
        /// 利用履歴詳細のコレクション（ナビゲーションプロパティ）
        /// </summary>
        public List<LedgerDetail> Details { get; set; } = new();

        /// <summary>
        /// 詳細件数（クエリで取得）
        /// </summary>
        public int DetailCount { get; set; }

        /// <summary>
        /// 導入行の種別（Issue #2371）。導入行でなければ <c>null</c>。
        /// </summary>
        /// <remarks>
        /// <c>CardManageViewModel.BuildInitialLedgerAsync</c>（カード登録）が書く。既存の行はマイグレーション 013 が
        /// 摘要から埋めた。行編集で摘要・日付を変えても、履歴の統合・分割でも変わらない
        /// （<c>LedgerRepository.UpdateAsync</c> の SET 句に含めない）。導入行かどうかの判定はすべてこの値で行い、
        /// 摘要（組織設定から生成した文字列）とは比べない（#2044 の <see cref="IsLentRecord"/> と同じ）。
        /// </remarks>
        public InitialRecordKind? InitialRecordKind { get; set; }

        // === ドメインロジック ===

        /// <summary>
        /// 繰越レコード（新規購入または年度途中繰越）かどうか
        /// </summary>
        /// <remarks>
        /// 帳票のソート順や日次集計で特別扱いされるレコードの判定。SQL 側の同等条件
        /// （<c>LedgerRepository</c> の繰越先頭ソートキー <c>initial_record_kind IN (1, 2)</c>）と対応する。
        /// Issue #2371: 以前は摘要（「新規購入」と組織設定の繰越パターン）で判定しており、設定を変えると
        /// 変更前に登録した行を取りこぼした。行自身の <see cref="InitialRecordKind"/> で判定する。
        /// </remarks>
        public bool IsCarryover =>
            InitialRecordKind is Models.InitialRecordKind.NewPurchase or Models.InitialRecordKind.MidYearCarryover;

        /// <summary>
        /// 年度途中導入の繰越レコード（「○月から繰越」）かどうか
        /// </summary>
        /// <remarks>
        /// 帳票の集計フィルタ（<c>ReportDataBuilder</c>）・受入欄の空欄制御（<c>ReportRowBuilder</c>）・
        /// ソート・残高チェーンの始点（<c>PaperCarryoverPolicy.GetChainRestartDates</c>）が使う。
        /// Issue #2371: 摘要と組織設定のパターン（<c>MidYearCarryoverPattern</c>）の照合から、行自身の種別へ移した。
        /// </remarks>
        public bool IsMidYearCarryover =>
            InitialRecordKind == Models.InitialRecordKind.MidYearCarryover;

        /// <summary>
        /// 3 月を繰越月にして登録したカードの導入行「前年度より繰越」かどうか（Issue #2326 / #2418 / #2371）
        /// </summary>
        /// <remarks>
        /// 帳票の「前年度より繰越」（通常は in-memory 合成）とは別物。DB に実在する導入行だけが真になる。
        /// </remarks>
        public bool IsFiscalYearStartCarryover =>
            InitialRecordKind == Models.InitialRecordKind.FiscalYearStartCarryover;

        /// <summary>
        /// 導入時（カード登録時）に書かれる行かどうか（Issue #2007）
        /// </summary>
        /// <remarks>
        /// <c>CardManageViewModel.BuildInitialLedgerAsync</c> が書く 3 種の行
        /// （「新規購入」／「○月から繰越」／3 月登録の「前年度より繰越」）を指す。
        /// <see cref="IsCarryover"/> は帳票の集計・ソート用で「前年度より繰越」を含まない
        /// （帳票の「前年度より繰越」は通常 in-memory 合成される行のため。ただし 3 月を繰越月にして
        /// 登録したカードでは DB に実在する導入行になる）が、導入時残高の誤りの検知では DB に実在する
        /// 導入行をすべて対象にする必要があるので別に定義する。
        /// Issue #2371: 判定は摘要ではなく <see cref="InitialRecordKind"/> で行う。
        /// </remarks>
        public bool IsInitialRecord => InitialRecordKind.HasValue;

        /// <summary>
        /// その種別の導入行が受入欄にも残高を書く行かどうか（Issue #2007）
        /// </summary>
        /// <remarks>
        /// <c>BuildInitialLedgerAsync</c> の <c>hasIncome</c> と同じ判断。新規購入と前年度より繰越は
        /// <c>Income = Balance</c>、「○月から繰越」は受入欄を空欄（0）にする（月次帳票の
        /// 受入欄に金額が入るのは 4 月の前年度繰越と新規購入のみ）。導入時残高を訂正するとき、
        /// 受入も一緒に直すかどうかはこの判定で決める。導入行でない（<c>null</c>）なら偽。
        /// </remarks>
        public static bool InitialRecordCarriesIncome(InitialRecordKind? kind) =>
            kind is Models.InitialRecordKind.NewPurchase or Models.InitialRecordKind.FiscalYearStartCarryover;
    }
}
