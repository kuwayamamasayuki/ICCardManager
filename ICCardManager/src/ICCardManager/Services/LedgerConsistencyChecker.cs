#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 残高チェーンの整合性をチェックするサービス（Issue #635）
    /// </summary>
    /// <remarks>
    /// 各行の残高が「前行の残高 + 受入 - 払出」と一致するかを検証します。
    /// 行の追加・削除・修正後に呼び出し、不整合があれば警告を表示します。
    /// Issue #1059: 詳細（LedgerDetail）レベルの残高チェーン検証も行います。
    /// </remarks>
    public class LedgerConsistencyChecker
    {
        private readonly ILedgerRepository _ledgerRepository;

        public LedgerConsistencyChecker(ILedgerRepository ledgerRepository)
        {
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// 指定期間の残高チェーンの整合性をチェック
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="fromDate">開始日</param>
        /// <param name="toDate">終了日</param>
        /// <returns>整合性チェック結果</returns>
        public async Task<ConsistencyResult> CheckBalanceConsistencyAsync(
            string cardIdm, DateTime fromDate, DateTime toDate)
        {
            // Issue #1004: 同一日内の順序を残高チェーンで決定する
            // ID順だとポイント還元と利用の順序が残高推移と一致せず、
            // 偽の不整合が報告される場合がある
            //
            // Issue #2112: 期間より前の最終残高をチェーン開始点のシードとして渡す。
            // 期間の最初の稼働日が同額のポイント還元と利用で残高が循環する日（#1004 形状）だと、
            // 当日の行だけでは開始点が決まらず id 順フォールバックへ落ちる。回転した並びは
            // 当日の中では整合して見えるが、当日の最終残高が中間残高になるため、
            // 正しい翌稼働日の行が不整合として報告される（期待値も存在しない残高になる）。
            // 2 日目以降は前の稼働日の最終残高が日をまたいで引き継がれるので、効くのは期間の初日だけ。
            // シードは確定済みの単票クエリ GetLatestBeforeDateAsync で取る（#2043 / #1763）。母集団は
            // 本体（GetByDateRangeAsync）と同じく貸出中レコードを含む。前の行が無ければ null（シード無しの従来挙動）。
            //
            // Issue #2418 / #2437: 年度の台帳が導入行「前年度より繰越」（3 月を繰越月にして登録したカード）から始まるなら、
            // 導入行の手前でチェーンを切り、導入行を始点にする。導入行の残高は登録時の繰越額で、前の行の残高から計算した値ではない。
            // 導入行より前の日付の行（3 月中の貸出中レコード・手入力・CSV）からチェーンを続けると、正しい導入行を不整合と報告し、
            // 導入行と同じ日の利用の順序も取り違える（前日の残高を引き継いで利用を先に並べる）。
            // #2418 は「開始日が導入行以前ならシードを使わない」で表示期間の検査だけを直し、全期間の検査（FullPeriodStart から）・
            // 3/1〜4/30 のように年度の初日より前から始まる範囲では導入行がチェーンの途中に来て今も報告されていた（#2437）。
            // 切る日は PaperCarryoverPolicy.GetChainRestartDates が決める。開始日の年度は履歴画面・帳票と同じ
            // PeriodOpensWithFiscalYearStartCarryover、後の年度は HasFiscalYearStartCarryoverRecord（#1763）。
            // Issue #2441: 年度途中で導入したカードの導入行「○月から繰越」（繰越月の翌月 1 日付け。登録日より後になり得る）の手前でも
            // 同じく切る。導入行より前の日付の返却の利用の行・手入力・CSV の行・貸出中レコードから続けて検査しない。
            // 既知の差: 4/1 から検査する場合でも、導入行を摘要で認識できない（#2371）カードでは、履歴は貸出中を除いた
            // 前年度繰越（GetCarryoverBalanceAsync）をシードにし、ここは貸出中を含む値を使う（本体の母集団に合わせる）。
            var periodLedgers = (await _ledgerRepository.GetByDateRangeAsync(cardIdm, fromDate, toDate).ConfigureAwait(false))
                .ToList();
            var fiscalYearFirstDayLedgers = await _ledgerRepository
                .GetFiscalYearFirstDayLedgersAsync(cardIdm, FiscalYearHelper.GetFiscalYear(fromDate)).ConfigureAwait(false);
            var restartDates = PaperCarryoverPolicy.GetChainRestartDates(fromDate, periodLedgers, fiscalYearFirstDayLedgers);
            var segments = SplitAtChainRestarts(periodLedgers, restartDates);

            // シードは最初のチェーン（最初の導入行より前の行）にだけ効く。導入行から始まるチェーンは前の残高を持たない
            var chains = new List<List<Ledger>>();
            for (var i = 0; i < segments.Count; i++)
            {
                int? precedingBalance = null;
                if (i == 0 && segments[0].Count > 0)
                {
                    precedingBalance = (await _ledgerRepository
                        .GetLatestBeforeDateAsync(cardIdm, fromDate).ConfigureAwait(false))?.Balance;
                }

                chains.Add(LedgerOrderHelper.ReorderByBalanceChain(segments[i], precedingBalance));
            }

            var ledgers = chains.SelectMany(c => c).ToList();

            // 導入行から始まるチェーンの先頭の、直前の帳簿の行（貸出中レコードを除く）。訂正案を出すかの判断にだけ使い、
            // 並べ替えのシード（上）とは分ける（#2418: 期間の開始日が導入行以前ならシードは使わない）。
            // 期間の外（4/1〜4/30 の表示期間なら 3/31 以前）にあっても DB から取る
            var chainHeadPredecessors = new List<Ledger?> { null };
            for (var i = 1; i < chains.Count; i++)
            {
                chainHeadPredecessors.Add(chains[i].Count == 0
                    ? null
                    : await _ledgerRepository.GetLatestSettledBeforeDateAsync(cardIdm, chains[i][0].Date.Date).ConfigureAwait(false));
            }

            // Issue #1059: 詳細レベルのチェックのためにDetailsを読み込む
            if (ledgers.Count > 0)
            {
                var ledgerIds = ledgers.Select(l => l.Id).ToList();
                var detailsMap = await _ledgerRepository.GetDetailsByLedgerIdsAsync(ledgerIds).ConfigureAwait(false);
                foreach (var ledger in ledgers)
                {
                    if (detailsMap.TryGetValue(ledger.Id, out var details))
                    {
                        ledger.Details = details;
                    }
                }
            }

            return CheckConsistency(chains, cardIdm, fromDate, chainHeadPredecessors);
        }

        /// <summary>
        /// Issue #2437 / #2441: 期間の台帳を、チェーンが始まり直す日（導入行「前年度より繰越」「○月から繰越」の日）の手前で分ける。
        /// 先頭の要素は最初の導入行より前の行（無ければ空）。
        /// </summary>
        internal static List<List<Ledger>> SplitAtChainRestarts(
            IEnumerable<Ledger> ledgers, IReadOnlyList<DateTime> restartDates)
        {
            var boundaries = restartDates.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
            var segments = new List<List<Ledger>>();
            for (var i = 0; i <= boundaries.Count; i++)
            {
                segments.Add(new List<Ledger>());
            }

            foreach (var ledger in ledgers)
            {
                // その行の日付以前に始まり直す日の数 ＝ 属するチェーンの番号
                var index = boundaries.Count(d => d <= ledger.Date.Date);
                segments[index].Add(ledger);
            }

            return segments;
        }

        /// <summary>
        /// 残高チェーンの整合性をチェック（内部ロジック）
        /// </summary>
        internal ConsistencyResult CheckConsistency(
            List<Ledger> ledgers, string cardIdm, DateTime fromDate)
        {
            return CheckConsistency(new[] { ledgers }, cardIdm, fromDate, new Ledger?[] { null });
        }

        /// <summary>
        /// 残高チェーンの整合性をチェック（内部ロジック）。チェーンごとに先頭の行を始点とし、チェーンをまたいで検査しない。
        /// </summary>
        /// <param name="chains">
        /// 時系列順に並べたチェーン。Issue #2437 / #2441: 導入行「前年度より繰越」「○月から繰越」から始まり直すチェーンは、
        /// その前の行（導入行より前の日付の行）の残高から続けて検査しない（<see cref="SplitAtChainRestarts"/>）
        /// </param>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="fromDate">開始日</param>
        /// <param name="chainHeadPredecessors">
        /// チェーンごとの、先頭の行の直前の帳簿の行（貸出中レコードを除く。期間の外でもよい）。無ければ null。
        /// 先頭のチェーン（期間の始まり）は null を渡す
        /// </param>
        internal ConsistencyResult CheckConsistency(
            IReadOnlyList<List<Ledger>> chains, string cardIdm, DateTime fromDate,
            IReadOnlyList<Ledger?> chainHeadPredecessors)
        {
            if (chainHeadPredecessors.Count != chains.Count)
            {
                throw new ArgumentException("チェーンと同じ数の直前の行を渡す", nameof(chainHeadPredecessors));
            }

            var result = new ConsistencyResult { IsConsistent = true };

            foreach (var ledgers in chains)
            {
                // 親レコードレベルのチェック
                // 期間の直前のレコードから前残高を取得する処理は非同期なので、
                // 最初の行は前行がないためスキップし、2行目以降をチェック
                for (int i = 1; i < ledgers.Count; i++)
                {
                    var previousBalance = ledgers[i - 1].Balance;
                    var current = ledgers[i];
                    var expectedBalance = previousBalance + current.Income - current.Expense;

                    if (current.Balance != expectedBalance)
                    {
                        result.IsConsistent = false;
                        result.Inconsistencies.Add((current.Id, expectedBalance, current.Balance));
                    }
                }

                // Issue #1059: 詳細レベルのチェック（明細のチェーンもチェーンの先頭から始める）
                CheckDetailConsistency(ledgers, result);
            }

            // Issue #2007: 導入時残高の誤りの形状なら、直すべき行と値を名指しする。
            // Issue #2437: 導入行は途中のチェーンの先頭にも来る（導入行より前の日付の行がある 3 月繰越のカード）
            // ただし、先頭の導入行の残高が直前の帳簿の行（貸出中レコードを除く。期間の外でもよい）の残高 ＋ 受入 − 払出 と一致する
            // （前の行で裏付けられている）なら、導入行は正しく、切れているのは直後の行の側なので訂正案を出さない（コードレビューで検出）。
            // 例: 前年度から台帳があるカードの 3/31 残高 0 → 4/1「前年度より繰越」受入・残高 5,000 → 4/2 の打ち間違い。
            // 訂正案を出すと、ハイライト（BuildInconsistencyMarkers）が正しい 4/1 だけになり、誤った 4/2 に印が付かない。
            // 直前の行は 4/1〜4/30 の表示期間でも期間の外から取る（全期間の検査と根拠を揃える）。貸出中レコードの残高は帳簿の残高ではない
            // ので数えない（3/25 の貸出中レコード残高 0 と、誤って 3,000 で登録した導入行を「裏付けあり」にしない）
            result.InitialBalanceCorrection = null;
            for (var i = 0; i < chains.Count; i++)
            {
                var ledgers = chains[i];
                if (ledgers.Count == 0 || IsCorroboratedByPrecedingSettledRow(chainHeadPredecessors[i], ledgers[0]))
                {
                    continue;
                }

                result.InitialBalanceCorrection = DetectInitialBalanceCorrection(ledgers, result);
                if (result.InitialBalanceCorrection != null)
                {
                    break;
                }
            }

            ReportUncorroboratedChainHeads(chains, chainHeadPredecessors, result);

            return result;
        }

        /// <summary>
        /// Issue #2441（コードレビューで検出）: 2 本目以降のチェーンの先頭（導入行）の残高の誤りが、訂正案の出ない形でも報告から消えないようにする。
        /// 先頭が直前の帳簿の行で裏付けられず、直後の行が不整合で、その先頭への訂正案が出ておらず、直後の行の単独の打ち間違いとも
        /// 説明できないなら、先頭も不整合として報告する。期待値は直後の行からの逆算（<see cref="ExpectedChainHeadBalance"/>）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// チェーンを切ると先頭の導入行は検査されないので、導入行の残高が誤っていても報告は直後の行（正しい行）にだけ付く。
        /// 不整合が 1 か所なら訂正案（#2007）が導入行を名指しするが、別の不整合がある・明細の差が合わない形では訂正案が出ない。
        /// </para>
        /// <para>
        /// 裏付けが無いこと自体は誤りの根拠にならない（年度途中の導入行は、導入行より前の行と残高が違うのが普通）。
        /// 直後の行が切れている原因は「先頭の誤り」と「直後の行の単独の打ち間違い」のどちらもあり得るので、後者と判別できる形は報告しない:
        /// </para>
        /// <list type="bullet">
        /// <item>次の次の行が、直後の行の「先頭から計算した期待残高」から続いて整合する（直後の行の誤りが次の行で打ち消されている）</item>
        /// <item>直後の行からの逆算が負（先頭をどう直しても直後の行は説明できない）</item>
        /// </list>
        /// <para>
        /// 限界（正しい先頭も報告し得る形）: 直後の行が期間の最後の行／次の次の行も切れている（直後と次の次の行の両方の打ち間違い）／
        /// 直後の行の受入・払出だけを誤った（残額はカードどおり。2/25 4,790 → 3/1 5,000 → 3/5 払出 250（正しくは 210）・残額 4,790 なら
        /// 先頭は「期待 5,040」と報告され、不整合がこの 1 か所なら #2007 の訂正案も先頭を 5,040 に直すよう誘導する）／
        /// 先頭と直後の行の間の行が欠けている（先頭の誤りと原理的に区別できない）。逆に、先頭と直後の行が同時に誤っている形は報告しない。
        /// 直前の帳簿の行が無いチェーン（期間の先頭のチェーン、導入行がカードの最初の帳簿の行で前に貸出中レコードしか無い等）は
        /// 先頭を報告しない（修正前と同じ）。
        /// </para>
        /// </remarks>
        internal static void ReportUncorroboratedChainHeads(
            IReadOnlyList<List<Ledger>> chains, IReadOnlyList<Ledger?> chainHeadPredecessors, ConsistencyResult result)
        {
            for (var i = 1; i < chains.Count; i++)
            {
                var ledgers = chains[i];
                var predecessor = chainHeadPredecessors[i];
                if (ledgers.Count < 2 || predecessor == null || IsCorroboratedByPrecedingSettledRow(predecessor, ledgers[0]))
                {
                    continue;
                }

                var head = ledgers[0];
                var next = ledgers[1];
                var nextIndex = result.Inconsistencies.FindIndex(x => x.LedgerId == next.Id);
                if (nextIndex < 0 || result.InitialBalanceCorrection?.LedgerId == head.Id)
                {
                    continue;
                }

                if (ledgers.Count >= 3 && IsIsolatedTypo(head, next, ledgers[2]))
                {
                    continue;
                }

                var expected = ExpectedChainHeadBalance(next);
                if (expected < 0)
                {
                    continue;
                }

                result.Inconsistencies.Insert(nextIndex, (head.Id, expected, head.Balance));
                result.IsConsistent = false;
            }
        }

        /// <summary>
        /// 直後の行の誤りが次の行で打ち消されているか（次の行が、直後の行の「先頭から計算した期待残高」から続いて整合する）。
        /// 真なら直後の行の単独の打ち間違いで、先頭は正しい。
        /// </summary>
        internal static bool IsIsolatedTypo(Ledger head, Ledger next, Ledger afterNext)
        {
            var expectedNext = head.Balance + next.Income - next.Expense;
            return expectedNext + afterNext.Income - afterNext.Expense == afterNext.Balance;
        }

        /// <summary>
        /// 報告するチェーンの先頭の期待残高。直後の行からの逆算（直後の残高 − 受入 ＋ 払出。#2007 の訂正案と同じ式）。
        /// </summary>
        /// <remarks>
        /// 直前の行からの計算（直前の残高 ＋ 受入 − 払出、または直前の残高）は使わない。年度途中の導入行は導入行より前の行と
        /// 残高が違うのが普通なので、その値に直すと直後の行が切れる（2/25 4,790 → 3/1 を 6,000 で誤登録 → 3/5 4,790 の正しい値は
        /// 3/5 からの逆算 5,000 で、2/25 の 4,790 ではない）。
        /// </remarks>
        internal static int ExpectedChainHeadBalance(Ledger next) => next.Balance - next.Income + next.Expense;

        /// <summary>
        /// Issue #2437: チェーンの先頭の行の残高が、直前の帳簿の行（貸出中レコードを除く）で裏付けられているか。直前の行が無ければ偽。
        /// </summary>
        /// <remarks>
        /// 裏付けの形は 2 つある（コードレビューで検出）。
        /// <list type="bullet">
        /// <item>直前の行の残高 ＋ 受入 − 払出 ＝ 先頭の残高（3/31「次年度へ繰越」で残高 0 にしてから 4/1「前年度より繰越」で受け入れる組）</item>
        /// <item>先頭が導入行（<see cref="Ledger.IsInitialRecord"/>）で、直前の行の残高 ＝ 先頭の残高。導入行の受入は新しいお金ではなく
        /// 残高の書き直しなので、直前の行（3/25 の手入力の行 残高 5,000 など）から引き継いだ残高と同じなら正しい。上の式だけだと
        /// 5,000 ＋ 5,000 ≠ 5,000 で裏付けが無いことになり、直後の行の打ち間違いに対して正しい導入行を書き直す訂正案を出していた。
        /// 受入欄に残高を書く導入行（前年度より繰越・新規購入）に加え、「○月から繰越」も含める（Issue #2441）。現行の受入 0 の行は
        /// 上の式と同じ判定になるが、#1219 より前の登録・CSV の取り込みで受入＝残高の行が残り得るため</item>
        /// </list>
        /// 限界: 直前の行の残高が、導入行の誤った残高と偶然一致する場合も裏付けありと判定する（訂正案は出ず、直後の行が強調される）。
        /// </remarks>
        internal static bool IsCorroboratedByPrecedingSettledRow(Ledger? precedingSettledRow, Ledger chainHead)
        {
            if (precedingSettledRow == null || precedingSettledRow.IsLentRecord)
            {
                return false;
            }

            if (precedingSettledRow.Balance + chainHead.Income - chainHead.Expense == chainHead.Balance)
            {
                return true;
            }

            return chainHead.IsInitialRecord
                && precedingSettledRow.Balance == chainHead.Balance;
        }

        /// <summary>
        /// Issue #2007: 残高チェーンの不整合が「導入時（カード登録時）の残高の誤り」の形状かを判定し、
        /// 該当すれば後続の行から逆算した正しい導入時残高を返す。該当しなければ null。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 導入行（<see cref="Ledger.IsInitialRecord"/>）は手入力の繰越額がカード実残高より優先されて
        /// 書かれる（<c>CardManageViewModel.BuildInitialLedgerAsync</c>）。以後の利用行の残額はカードの
        /// 実残高に追随するため、初期残高の誤りは導入行 1 行に閉じ、チェーンは<b>導入行の直後の
        /// 1 か所だけ</b>で切れる。この形状を満たす条件:
        /// </para>
        /// <list type="bullet">
        /// <item>先頭行が導入行で、2 行目が存在する</item>
        /// <item>親レコードの不整合がちょうど 1 件で、それが 2 行目</item>
        /// <item>詳細レベルの不整合は無いか、2 行目の 1 件だけで差が親と一致する
        /// （導入行の残高を起点に検査される先頭明細の写像）</item>
        /// <item>逆算した残高（2 行目の残額 − 受入 ＋ 払出）が 0 以上</item>
        /// </list>
        /// <para>
        /// 従来はチェーンが切れた側（2 行目＝正しい行）だけがハイライトされ、利用者が正しい行を
        /// 誤った導入行に合わせて書き換える誘導になっていた。ここで名指しする「直すべき行」は導入行。
        /// 不整合が 2 か所以上ある・先頭が導入行でない・逆算が負になる形は、1 行の訂正では直らない
        /// （または導入時の誤りと決めつけられない）ので提案しない。
        /// </para>
        /// </remarks>
        internal static InitialBalanceCorrection? DetectInitialBalanceCorrection(
            List<Ledger> ledgers, ConsistencyResult result)
        {
            if (ledgers.Count < 2)
            {
                return null;
            }

            var initial = ledgers[0];
            var next = ledgers[1];
            if (!initial.IsInitialRecord)
            {
                return null;
            }

            if (result.Inconsistencies.Count != 1 || result.Inconsistencies[0].LedgerId != next.Id)
            {
                return null;
            }

            var suggested = next.Balance - next.Income + next.Expense;
            if (suggested < 0)
            {
                return null;
            }

            var delta = initial.Balance - suggested;
            if (result.DetailInconsistencies.Count > 1)
            {
                return null;
            }

            if (result.DetailInconsistencies.Count == 1)
            {
                var detail = result.DetailInconsistencies[0];
                if (detail.LedgerId != next.Id)
                {
                    return null;
                }

                if (detail.ExpectedBalance - detail.ActualBalance != delta)
                {
                    return null;
                }
            }

            return new InitialBalanceCorrection(
                ledgerId: initial.Id,
                date: initial.Date,
                recordedBalance: initial.Balance,
                suggestedBalance: suggested,
                appliesToIncome: Ledger.InitialRecordCarriesIncome(initial.Summary));
        }

        /// <summary>
        /// Issue #1059: 詳細（LedgerDetail）レベルの残高チェーン整合性をチェック
        /// </summary>
        /// <remarks>
        /// 各Ledger内のDetail間、および連続するLedger間のDetail残高チェーンを検証します。
        /// 検証式: チャージ/ポイント還元の場合 → 前の残額 + 金額 = 次の残額
        ///         通常利用の場合 → 前の残額 - 金額 = 次の残額
        /// </remarks>
        internal static void CheckDetailConsistency(List<Ledger> ledgers, ConsistencyResult result)
        {
            // 全Ledgerの詳細を時系列順に連結してチェーン検証
            int? previousDetailBalance = null;
            int previousLedgerId = -1;

            foreach (var ledger in ledgers)
            {
                if (ledger.Details == null || ledger.Details.Count == 0)
                {
                    // 詳細がないLedgerの場合、親の残高を前残高として引き継ぐ
                    previousDetailBalance = ledger.Balance;
                    previousLedgerId = ledger.Id;
                    continue;
                }

                foreach (var detail in ledger.Details)
                {
                    if (!detail.Amount.HasValue || !detail.Balance.HasValue)
                    {
                        // 金額/残額がnullの詳細はスキップ
                        continue;
                    }

                    if (previousDetailBalance.HasValue)
                    {
                        var expected = CalculateExpectedDetailBalance(
                            previousDetailBalance.Value, detail);

                        if (detail.Balance.Value != expected)
                        {
                            result.IsConsistent = false;
                            result.DetailInconsistencies.Add(new DetailInconsistency
                            {
                                LedgerId = ledger.Id,
                                SequenceNumber = detail.SequenceNumber,
                                ExpectedBalance = expected,
                                ActualBalance = detail.Balance.Value
                            });
                        }
                    }

                    previousDetailBalance = detail.Balance.Value;
                    previousLedgerId = ledger.Id;
                }
            }
        }

        /// <summary>
        /// 詳細レコードの期待残高を計算
        /// </summary>
        internal static int CalculateExpectedDetailBalance(int previousBalance, LedgerDetail detail)
        {
            // 呼び出し元（CheckDetailConsistency）は金額が null の明細を読み飛ばしてから呼ぶ
            var amount = detail.Amount ?? throw new ArgumentException("金額が null の明細は期待残高を計算できない", nameof(detail));
            if (detail.IsCharge || detail.IsPointRedemption)
            {
                // チャージ・ポイント還元: 残高が増加
                return previousBalance + amount;
            }
            else
            {
                // 通常利用（鉄道・バス）: 残高が減少
                return previousBalance - amount;
            }
        }
    }

    /// <summary>
    /// 残高整合性チェック結果
    /// </summary>
    public class ConsistencyResult
    {
        /// <summary>
        /// 整合性があるかどうか
        /// </summary>
        public bool IsConsistent { get; set; }

        /// <summary>
        /// 不整合箇所リスト（LedgerId, ExpectedBalance, ActualBalance）
        /// </summary>
        public List<(int LedgerId, int ExpectedBalance, int ActualBalance)> Inconsistencies { get; set; } = new();

        /// <summary>
        /// Issue #1059: 詳細レベルの不整合箇所リスト
        /// </summary>
        public List<DetailInconsistency> DetailInconsistencies { get; set; } = new();

        /// <summary>
        /// Issue #2007: 不整合が「導入時残高の誤り」の形状であるときの訂正案。該当しなければ null。
        /// </summary>
        /// <remarks>
        /// <see cref="LedgerConsistencyChecker.DetectInitialBalanceCorrection"/> が
        /// <see cref="Inconsistencies"/> / <see cref="DetailInconsistencies"/> から導出する。
        /// 導出値なので、それらを書き換えた後は再判定が要る。
        /// </remarks>
        public InitialBalanceCorrection? InitialBalanceCorrection { get; set; }
    }

    /// <summary>
    /// Issue #2007: 導入時（カード登録時）の残高の誤りに対する訂正案
    /// </summary>
    /// <remarks>
    /// 不変オブジェクト。「直すべき行」「記録されている残高」「後続の行から逆算した残高」
    /// 「受入欄も一緒に直すか」を 1 つにまとめ、消費側（警告文言・ハイライト・行編集ダイアログ）が
    /// 別々に逆算し直さないようにする（#1763「同じ判断を配らない」）。
    /// </remarks>
    public sealed class InitialBalanceCorrection
    {
        public InitialBalanceCorrection(int ledgerId, DateTime date, int recordedBalance, int suggestedBalance, bool appliesToIncome)
        {
            if (suggestedBalance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(suggestedBalance), suggestedBalance, "逆算した残高は 0 以上でなければならない");
            }

            LedgerId = ledgerId;
            Date = date;
            RecordedBalance = recordedBalance;
            SuggestedBalance = suggestedBalance;
            AppliesToIncome = appliesToIncome;
        }

        /// <summary>直すべき導入行の ID</summary>
        public int LedgerId { get; }

        /// <summary>導入行の日付（履歴表示の期間をここから始めるために使う）</summary>
        public DateTime Date { get; }

        /// <summary>導入行に記録されている残高（誤っている疑いのある値）</summary>
        public int RecordedBalance { get; }

        /// <summary>直後の行から逆算した残高（直後の残額 − 受入 ＋ 払出）</summary>
        public int SuggestedBalance { get; }

        /// <summary>
        /// 受入欄も <see cref="SuggestedBalance"/> へ直すか。
        /// 新規購入・前年度より繰越は真（受入欄に残高を書く）、○月から繰越は偽（受入欄は空欄）。
        /// <see cref="Ledger.InitialRecordCarriesIncome"/> と同じ判断。
        /// </summary>
        public bool AppliesToIncome { get; }
    }

    /// <summary>
    /// Issue #1059: 詳細レベルの残高不整合情報
    /// </summary>
    public class DetailInconsistency
    {
        /// <summary>
        /// 親LedgerのID
        /// </summary>
        public int LedgerId { get; set; }

        /// <summary>
        /// 詳細のシーケンス番号（id）
        /// </summary>
        public int SequenceNumber { get; set; }

        /// <summary>
        /// 期待される残高
        /// </summary>
        public int ExpectedBalance { get; set; }

        /// <summary>
        /// 実際の残高
        /// </summary>
        public int ActualBalance { get; set; }
    }
}
