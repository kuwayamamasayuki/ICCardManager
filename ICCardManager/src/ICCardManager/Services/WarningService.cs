using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// データ系の警告チェックを担当するサービス
    /// </summary>
    /// <remarks>
    /// MainViewModelから抽出。残額警告とバス停未入力チェックを一元化。
    /// インフラ系の警告（接続断・カードリーダー）はMainViewModelに残す。
    /// </remarks>
    public class WarningService
    {
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IDatabaseInfo _databaseInfo;
        private readonly IUpdateNotificationService _updateNotificationService;
        private readonly IBackupHealthService _backupHealthService;
        private readonly ICarryoverDataLossDetector _carryoverDataLossDetector;

        /// <param name="ledgerRepository">台帳リポジトリ</param>
        /// <param name="databaseInfo">DB接続情報</param>
        /// <param name="updateNotificationService">
        /// 更新通知チェック（Issue #1687）。null の場合、更新通知警告は常に生成されない
        /// （既存テストの構築コードとの互換のため省略可能にしている。DI経由では常に注入される）
        /// </param>
        /// <param name="backupHealthService">
        /// バックアップ健全性チェック（Issue #1689）。null の場合、バックアップ警告は常に生成されない
        /// （updateNotificationService と同じ理由で省略可能）
        /// </param>
        /// <param name="carryoverDataLossDetector">
        /// 繰越情報消失の検出（Issue #1758）。null の場合、繰越情報消失警告は常に生成されない
        /// （updateNotificationService と同じ理由で省略可能）
        /// </param>
        public WarningService(
            ILedgerRepository ledgerRepository,
            IDatabaseInfo databaseInfo,
            IUpdateNotificationService updateNotificationService = null,
            IBackupHealthService backupHealthService = null,
            ICarryoverDataLossDetector carryoverDataLossDetector = null)
        {
            _ledgerRepository = ledgerRepository;
            _databaseInfo = databaseInfo;
            _updateNotificationService = updateNotificationService;
            _backupHealthService = backupHealthService;
            _carryoverDataLossDetector = carryoverDataLossDetector;
        }

        /// <summary>
        /// ダッシュボードデータから残額警告を生成
        /// </summary>
        /// <param name="dashboardItems">ダッシュボードアイテム一覧</param>
        /// <param name="warningBalance">警告しきい値（円）</param>
        /// <returns>残額警告のリスト</returns>
        public IReadOnlyList<WarningItem> CheckLowBalanceWarnings(
            IEnumerable<CardBalanceDashboardItem> dashboardItems,
            int warningBalance)
        {
            var warnings = new List<WarningItem>();
            foreach (var item in dashboardItems)
            {
                // Issue: DashboardService.BuildDashboardAsync と判定条件を統一する。
                // DashboardService側は IsBalanceWarning = balance <= warningBalance (≤) で
                // 警告アイコンを出しているため、警告一覧も同じ条件 (≤) でないと
                // 「アイコンは出るが一覧に載らない」という不整合が発生する。
                if (item.CurrentBalance <= warningBalance)
                {
                    warnings.Add(new WarningItem
                    {
                        DisplayText = $"⚠️ {item.CardType} {item.CardNumber}: 残額 {DisplayFormatters.FormatBalanceWithUnit(item.CurrentBalance)}（しきい値: {warningBalance:N0}円）",
                        Type = WarningType.LowBalance,
                        CardIdm = item.CardIdm
                    });
                }
            }
            return warnings;
        }

        /// <summary>
        /// バス停名未入力の件数をチェック
        /// </summary>
        /// <returns>未入力件数がある場合はWarningItem、ない場合はnull</returns>
        public async Task<WarningItem> CheckIncompleteBusStopsAsync()
        {
            var ledgers = await _ledgerRepository.GetByDateRangeAsync(
                null, DateTime.Now.AddYears(-1), DateTime.Now).ConfigureAwait(false);

            var incompleteCount = ledgers.Count(l => l.Summary?.Contains("★") == true);
            if (incompleteCount > 0)
            {
                return new WarningItem
                {
                    DisplayText = $"⚠️ バス停名が未入力の履歴が{incompleteCount}件あります",
                    Type = WarningType.IncompleteBusStop
                };
            }
            return null;
        }

        /// <summary>
        /// ジャーナルモード警告を生成
        /// </summary>
        /// <returns>ジャーナルモードが低下している場合はWarningItem、正常な場合はnull</returns>
        public WarningItem CheckJournalModeWarning()
        {
            if (!_databaseInfo.IsJournalModeDegraded)
                return null;

            return new WarningItem
            {
                Type = WarningType.DatabaseJournalModeDegraded,
                DisplayText = $"⚠️ データベースのクラッシュ耐性が低下しています（journal_mode={_databaseInfo.CurrentJournalMode}）。" +
                              "ファイルサーバ管理者にご相談ください。"
            };
        }

        /// <summary>
        /// 更新通知警告を生成（Issue #1687）
        /// </summary>
        /// <remarks>
        /// DBと同じフォルダの latest_version.txt に自バージョンより新しいバージョンが
        /// 記載されている場合、更新を促す通知を生成する。ファイル読み取りを伴うため、
        /// UI スレッドから呼ぶ場合は Task.Run 経由を推奨（SMB遅延対策）。
        /// </remarks>
        /// <returns>新しいバージョンがある場合はWarningItem、ない場合はnull</returns>
        public WarningItem CheckUpdateNotificationWarning()
        {
            var result = _updateNotificationService?.CheckForNewerVersion();
            if (result == null)
                return null;

            return new WarningItem
            {
                Type = WarningType.NewVersionAvailable,
                DisplayText = $"ℹ️ 新しいバージョン {result.LatestVersion} が公開されています" +
                              $"（このPCは {result.CurrentVersion}）。管理者に更新をご確認ください。"
            };
        }

        /// <summary>
        /// バックアップ健全性警告を生成（Issue #1689）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「最終成功からの経過日数」で判定する。起動時に自動バックアップが走る設計のため
        /// 「連続失敗回数」でも検知できそうに見えるが、長期休暇などでアプリを起動しなかった期間は
        /// 失敗回数が増えないまま古いバックアップだけが残る。経過日数ならこの穴を塞げる。
        /// </para>
        /// <para>
        /// 成功記録が一度もない場合（＝Issue #1689 導入前からの既存環境の初回起動時）は
        /// 警告しない。判断材料がない状態で警告を出すと、実際には正常な環境でも
        /// 必ず警告が出てしまい「オオカミ少年」になるため。
        /// </para>
        /// <para>
        /// settings の読み取りとバックアップフォルダ走査（共有モードでは SMB アクセス）を伴うため、
        /// UI スレッドから呼ぶ場合は Task.Run 経由を推奨。
        /// </para>
        /// </remarks>
        /// <param name="now">現在日時（テスト容易性のため引数で受け取る）</param>
        /// <returns>しきい値を超えて成功していない場合は WarningItem、正常な場合は null</returns>
        public async Task<WarningItem> CheckBackupHealthWarningAsync(DateTime now)
        {
            if (_backupHealthService == null)
                return null;

            var health = await _backupHealthService.GetHealthAsync().ConfigureAwait(false);
            var elapsedDays = health?.GetDaysSinceLastSuccess(now);
            if (elapsedDays == null || elapsedDays <= AppConstants.BackupStaleWarningDays)
                return null;

            return new WarningItem
            {
                Type = WarningType.BackupStale,
                DisplayText =
                    $"⚠️ 自動バックアップが{elapsedDays}日間成功していません" +
                    $"（最終成功: {DisplayFormatters.FormatDateTime(health.LastSuccessAt)}）。" +
                    "保存先フォルダーの空き容量やアクセス権に問題がある可能性があります。" +
                    "システム管理画面（F6）でバックアップ状況を確認し、手動バックアップを実行してください。"
            };
        }

        /// <summary>
        /// 繰越情報消失警告を生成（Issue #1758）
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #1726 以前の <c>UPDATE ic_card</c> で紙出納簿移行カード（Issue #510 / #1215）の
        /// 繰越累計・開始ページ番号が既定値へ落ちた被害を通知する。復旧手段は持たず、
        /// 「被害の有無を利用者が自力で知れること」を価値とする（Issue #1758 の案A）。
        /// </para>
        /// <para>
        /// operation_log の走査（共有モードでは SMB アクセス）を伴うため、
        /// UI スレッドから呼ぶ場合は Task.Run 経由を推奨。
        /// </para>
        /// </remarks>
        /// <returns>被害があれば WarningItem、なければ null</returns>
        public async Task<WarningItem> CheckCarryoverDataLossWarningAsync()
        {
            if (_carryoverDataLossDetector == null)
                return null;

            var items = await _carryoverDataLossDetector.DetectAsync().ConfigureAwait(false);
            if (items == null || items.Count == 0)
                return null;

            return new WarningItem
            {
                Type = WarningType.CarryoverDataLoss,
                DisplayText =
                    $"⚠️ 紙の出納簿から移行したカード{items.Count}枚（{FormatCardNames(items)}）の" +
                    "繰越累計・開始ページ番号が失われています。" +
                    "過去のバージョンでカード情報を編集した際に消去されたため、" +
                    "月次帳票（物品出納簿）の年度累計とページ番号が正しく出力されません。" +
                    "この警告をクリックして失われた値を確認し、" +
                    "システム管理者にデータベースの修正を依頼してください。"
            };
        }

        /// <summary>
        /// 警告文言へ載せるカード名を組み立てる。
        /// 先頭 <see cref="AppConstants.CarryoverDataLossWarningMaxListedCards"/> 枚を名前で示し、
        /// 残りは「ほか○枚」に畳む。
        /// </summary>
        private static string FormatCardNames(IReadOnlyList<CarryoverDataLossItem> items)
        {
            var listed = items
                .Take(AppConstants.CarryoverDataLossWarningMaxListedCards)
                .Select(i => i.CardDisplayName);
            var names = string.Join("、", listed);

            var remaining = items.Count - AppConstants.CarryoverDataLossWarningMaxListedCards;
            return remaining > 0 ? $"{names} ほか{remaining}枚" : names;
        }
    }
}
