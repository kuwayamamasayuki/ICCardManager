using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 繰越情報（開始ページ番号・繰越累計）の消失を検出するサービス（Issue #1758）
    /// </summary>
    public interface ICarryoverDataLossDetector
    {
        /// <summary>
        /// 繰越情報を失ったまま復旧されていないカードを検出する
        /// </summary>
        /// <returns>検出結果。被害がなければ空のリスト</returns>
        Task<IReadOnlyList<CarryoverDataLossItem>> DetectAsync();
    }

    /// <summary>
    /// 繰越情報（開始ページ番号・繰越累計）の消失を検出するサービス（Issue #1758）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>汎用コアに属する</b>（.claude/rules/domain-boundaries.md）。扱うのは
    /// 「物品出納簿の繰越・ページ番号」と「操作ログ」だけで、駅・バス・チャージといった
    /// 交通系固有の語彙で分岐しない。
    /// </para>
    /// <para>
    /// <b>なぜ現在値だけでは判定できないか</b>: 検出対象の4項目はいずれも既定値
    /// （<see cref="IcCard.StartingPageNumber"/>=1 / 繰越累計=0 / 対象年度=null）を取り得るため、
    /// 「元から既定値のカード」と「Issue #1726 以前の UPDATE で既定値へ落ちたカード」を
    /// 現在の DB からは区別できない。そこで <c>operation_log</c> の BeforeData / AfterData を
    /// 突き合わせ、**非既定値から既定値へ落ちた瞬間**を証拠として検出する。
    /// </para>
    /// <para>
    /// <b>検出できない範囲</b>: <c>operation_log</c> は6年で物理削除されるため、それ以前の消失は
    /// 検出できない。また操作ログを書かない経路での更新も対象外。案A（検知のみ）の割り切りであり、
    /// 復旧手段は提供しない（Issue #1758 の案B のスコープ）。
    /// </para>
    /// </remarks>
    public class CarryoverDataLossDetector : ICarryoverDataLossDetector
    {
        /// <summary>開始ページ番号の既定値（<see cref="IcCard.StartingPageNumber"/> の初期値と一致させる）</summary>
        private const int DefaultStartingPageNumber = 1;

        /// <summary>繰越累計金額の既定値</summary>
        private const int DefaultCarryoverTotal = 0;

        private readonly IOperationLogRepository _operationLogRepository;
        private readonly ICardRepository _cardRepository;

        public CarryoverDataLossDetector(
            IOperationLogRepository operationLogRepository,
            ICardRepository cardRepository)
        {
            _operationLogRepository = operationLogRepository;
            _cardRepository = cardRepository;
        }

        /// <summary>
        /// 繰越情報を失ったまま復旧されていないカードを検出する
        /// </summary>
        /// <remarks>
        /// 母集団は有効なカード（<c>is_deleted = 0</c>）のみ。論理削除済みカードは帳票の対象外で、
        /// 復旧しても意味がないため除外する（これにより「カードを削除したのに警告が残る」も起きない）。
        /// operation_log の全件走査と SMB アクセスを伴うため、UI スレッドから呼ぶ場合は Task.Run 経由を推奨。
        /// </remarks>
        /// <returns>検出結果（消失の発生が古い順）。被害がなければ空のリスト</returns>
        public async Task<IReadOnlyList<CarryoverDataLossItem>> DetectAsync()
        {
            var logs = await _operationLogRepository.SearchAllAsync(new OperationLogSearchCriteria
            {
                TargetTable = OperationLogger.Tables.IcCard,
                Action = OperationLogger.Actions.Update
            }).ConfigureAwait(false);

            var cards = await _cardRepository.GetAllAsync().ConfigureAwait(false);

            var currentCards = (cards ?? Enumerable.Empty<IcCard>())
                .Where(c => c != null && !string.IsNullOrEmpty(c.CardIdm))
                .GroupBy(c => c.CardIdm)
                .ToDictionary(g => g.Key, g => g.First());

            // 「検出済みのカード」の判定と「返す順序」を分けて持つ。
            // Dictionary の列挙順は言語仕様上の保証がなく、一方で並び順は警告文言へ載せる
            // 先頭数枚（WarningService.FormatCardNames）の選択に効くため、順序は List で明示する。
            var detectedIdms = new HashSet<string>();
            var detected = new List<CarryoverDataLossItem>();

            // 時系列順に走査し、カードごとに「最初の消失」だけを採る。2回目以降の UPDATE では
            // BeforeData も既に既定値へ落ちているため、最新のログを採ると失われた値として
            // 既定値（1 / 0 / 0 / null）を提示してしまう。
            //
            // この「最初の1件で打ち切る」形は、**1回の UPDATE で4項目が同時に落ちる**という
            // 前提に依存する。Issue #1726 以前の CardManageViewModel は画面入力だけから組んだ
            // IcCard を渡していたため4項目が必ず同時に既定値へ落ちており、ic_card へ UPDATE ログを
            // 書く経路は当時これ1つだった（CSV インポートは一括の IMPORT ログを書く）。
            // したがって「別々のログで異なる項目が落ちる」形は現状では発生しない。
            // ic_card の UPDATE ログを書く経路を増やすときは、この前提が崩れていないか確認すること
            // （崩れる場合は複数ログの消失項目をマージする必要があり、LostAt / OperatorName を
            // どの操作のものにするかという別の判断も伴う）。
            var orderedLogs = (logs ?? Enumerable.Empty<OperationLog>())
                .Where(l => l != null && !string.IsNullOrEmpty(l.TargetId))
                .OrderBy(l => l.Timestamp)
                .ThenBy(l => l.Id);

            foreach (var log in orderedLogs)
            {
                if (detectedIdms.Contains(log.TargetId))
                    continue;

                if (!currentCards.TryGetValue(log.TargetId, out var current))
                    continue;

                var before = TryDeserializeCard(log.BeforeData);
                var after = TryDeserializeCard(log.AfterData);
                if (before == null || after == null)
                    continue;

                var item = BuildLossItem(log, before, after, current);
                if (item != null)
                {
                    detectedIdms.Add(log.TargetId);
                    detected.Add(item);
                }
            }

            return detected;
        }

        /// <summary>
        /// 1件のログから消失項目を組み立てる。消失が1項目も無ければ null を返す。
        /// </summary>
        private static CarryoverDataLossItem BuildLossItem(
            OperationLog log, IcCard before, IcCard after, IcCard current)
        {
            var lostStartingPage = DetectLostInt(
                before.StartingPageNumber, after.StartingPageNumber,
                current.StartingPageNumber, DefaultStartingPageNumber);

            var lostIncomeTotal = DetectLostInt(
                before.CarryoverIncomeTotal, after.CarryoverIncomeTotal,
                current.CarryoverIncomeTotal, DefaultCarryoverTotal);

            var lostExpenseTotal = DetectLostInt(
                before.CarryoverExpenseTotal, after.CarryoverExpenseTotal,
                current.CarryoverExpenseTotal, DefaultCarryoverTotal);

            var lostFiscalYear = DetectLostFiscalYear(
                before.CarryoverFiscalYear, after.CarryoverFiscalYear, current.CarryoverFiscalYear);

            if (lostStartingPage == null && lostIncomeTotal == null
                && lostExpenseTotal == null && lostFiscalYear == null)
            {
                return null;
            }

            return new CarryoverDataLossItem
            {
                CardIdm = current.CardIdm,
                CardDisplayName = current.DisplayName,
                LostStartingPageNumber = lostStartingPage,
                LostCarryoverIncomeTotal = lostIncomeTotal,
                LostCarryoverExpenseTotal = lostExpenseTotal,
                LostCarryoverFiscalYear = lostFiscalYear,
                LostAt = log.Timestamp,
                OperatorName = log.OperatorName
            };
        }

        /// <summary>
        /// 数値項目が「非既定値 → 既定値」へ落ち、かつ現在も既定値のままなら、失われた元の値を返す。
        /// </summary>
        /// <remarks>
        /// 現在値の確認が要点。これが無いと、DB を直接修正して復旧した後も
        /// 6年間残る消失ログを根拠に警告が出続ける（Issue #1739 の「復旧したのに消えない」）。
        /// </remarks>
        private static int? DetectLostInt(int before, int after, int current, int defaultValue)
        {
            if (before == defaultValue) return null;    // 元から既定値＝消失ではない
            if (after != defaultValue) return null;     // 既定値へ落ちていない
            if (current != defaultValue) return null;   // 既に復旧済み
            return before;
        }

        /// <summary>
        /// <see cref="IcCard.CarryoverFiscalYear"/>（null 可）に対する <see cref="DetectLostInt"/> 相当。
        /// </summary>
        private static int? DetectLostFiscalYear(int? before, int? after, int? current)
        {
            if (before == null) return null;
            if (after != null) return null;
            if (current != null) return null;
            return before;
        }

        /// <summary>
        /// 操作ログの JSON をカードへ復元する。解釈できない場合は null（呼び出し元でスキップ）。
        /// </summary>
        /// <remarks>
        /// 6年分の過去ログには旧バージョンが書いた形式や、途中で切れた内容が混ざり得る。
        /// 1行の破損で検出全体を止めないため、例外は握りつぶさずに「この行は判定材料にしない」へ畳む。
        /// 握りつぶしても失われるのは1行分の判定材料だけで、他の行の検出は継続する。
        /// </remarks>
        private static IcCard TryDeserializeCard(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<IcCard>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
