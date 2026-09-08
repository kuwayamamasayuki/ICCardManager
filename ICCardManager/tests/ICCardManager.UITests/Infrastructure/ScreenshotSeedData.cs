using System;
using System.Data.SQLite;
using System.Globalization;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// マニュアル用スクリーンショット（Issue #2016）で画面に表示するサンプルデータ。
    /// <see cref="AppFixture.LaunchWithSeed"/> へ渡してマイグレーション済みの空 DB に投入する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 職員 2 名・交通系ICカード 3 枚（通常／貸出中／残額不足）と、通常カードの当月分の利用履歴を作る。
    /// 履歴画面は既定で「今月」を表示する（<c>MainViewModel.ShowHistoryAsync</c>）ため、
    /// 利用日は当月に収める。残高チェーン（前行の残額 ＋ 受入 − 払出 ＝ 当行の残額）を崩すと
    /// 不整合警告が写り込むので、残額は投入時に順に計算する。
    /// </para>
    /// <para>
    /// UITests プロジェクトは本体を参照しない（csproj の方針）ため、列名・日付書式・摘要はリテラルで持つ。
    /// 日付書式は <c>Common/SqliteDateTimeFormat.DateTimePattern</c>（<c>yyyy-MM-dd HH:mm:ss</c>）、
    /// 摘要は <c>SummaryGenerator</c> の生成結果と同じ形に揃えている。本体側の形式を変えたら本クラスも追随すること。
    /// </para>
    /// </remarks>
    internal static class ScreenshotSeedData
    {
        /// <summary>SQLite へ書く日時の書式（本体の <c>SqliteDateTimeFormat.DateTimePattern</c> と同じ）。</summary>
        private const string DateTimePattern = "yyyy-MM-dd HH:mm:ss";

        /// <summary>通常カード（履歴画面の撮影対象）。07_テスト設計書 §10.2 のサンプルと同じ IDm。</summary>
        public const string NormalCardIdm = "07FE11111111";
        public const string NormalCardType = "はやかけん";
        public const string NormalCardNumber = "001";

        /// <summary>貸出中カード。</summary>
        public const string LentCardIdm = "05FE22222222";
        public const string LentCardType = "nimoca";
        public const string LentCardNumber = "002";

        /// <summary>残額不足カード（既定のしきい値 10,000 円以下）。</summary>
        public const string LowBalanceCardIdm = "06FE33333333";
        public const string LowBalanceCardType = "SUGOCA";
        public const string LowBalanceCardNumber = "003";

        /// <summary>カード一覧の行に付く AutomationProperties.Name（<c>IcCard.DisplayName</c> ＝ 「種別 管理番号」）。</summary>
        public static string NormalCardDisplayName => $"{NormalCardType} {NormalCardNumber}";

        /// <summary>主担当の職員。IDm は仮想タッチと一致する <see cref="AppFixture.SeededStaffIdm"/>。</summary>
        public const string PrimaryStaffName = "博多 花子";
        public const string SecondaryStaffIdm = "FFFF000000000002";
        public const string SecondaryStaffName = "天神 太郎";

        /// <summary>
        /// DEBUG パネルの「交通系ICカード」ボタンが模擬する IDm（<c>MainViewModel.SimulateIcCard</c> と一致させる）。
        /// 第 2 段階（Issue #2019）の貸出・返却撮影で使う。
        /// </summary>
        public const string VirtualTouchCardIdm = "07FE112233445566";
        public const string VirtualTouchCardType = "はやかけん";
        public const string VirtualTouchCardNumber = "004";
        public static string VirtualTouchCardDisplayName => $"{VirtualTouchCardType} {VirtualTouchCardNumber}";

        /// <summary>
        /// 第 2 段階（タッチを要する画面）用のサンプルデータ。<see cref="Seed"/> に加えて、
        /// 仮想タッチ用のカードを未貸出で投入し、返却時の同行者数ダイアログ（#2009）をスキップする設定を書く
        /// （自動で閉じるまで既定 30 秒待つと、その間にトーストが消える）。
        /// </summary>
        public static void SeedForVirtualTouch(SQLiteConnection conn) => SeedForVirtualTouch(conn, skipBusStopInput: true);

        /// <param name="conn">接続。</param>
        /// <param name="skipBusStopInput">
        /// 返却後のバス停名入力ダイアログを出さない設定を書くか。返却トーストの撮影ではダイアログが
        /// トーストの直後に重なるため true、バス停名入力ダイアログ自体を撮るときは false。
        /// </param>
        public static void SeedForVirtualTouch(SQLiteConnection conn, bool skipBusStopInput)
        {
            Seed(conn);

            using var tx = conn.BeginTransaction();
            InsertCard(conn, VirtualTouchCardIdm, VirtualTouchCardType, VirtualTouchCardNumber, isLent: false, lentAt: null, lentStaff: null);
            // 残額警告のしきい値（既定 10,000 円）を十分に上回る残高にし、トーストに「残額不足」が出ないようにする
            _ = InsertLedger(conn, VirtualTouchCardIdm, DayOfMonth(DateTime.Today, 1), "新規購入", 20000, 0, 0, PrimaryStaffName);

            // settings は key/value。キーは SettingsRepository.KeySkipCompanionCountInputOnReturn、値は "true" 判定
            Execute(conn,
                "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
                ("@key", "skip_companion_count_input_on_return"), ("@value", "true"));
            Execute(conn,
                "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
                ("@key", "skip_bus_stop_input_on_return"), ("@value", skipBusStopInput ? "true" : "false"));
            tx.Commit();
        }

        /// <summary>
        /// サンプルデータを投入する。
        /// </summary>
        public static void Seed(SQLiteConnection conn)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            var today = DateTime.Today;
            var now = DateTime.Now;

            using var tx = conn.BeginTransaction();

            Execute(conn,
                "INSERT OR IGNORE INTO staff (staff_idm, name, number, note, is_deleted) VALUES (@idm, @name, @number, NULL, 0)",
                ("@idm", AppFixture.SeededStaffIdm), ("@name", PrimaryStaffName), ("@number", "100001"));
            Execute(conn,
                "INSERT OR IGNORE INTO staff (staff_idm, name, number, note, is_deleted) VALUES (@idm, @name, @number, NULL, 0)",
                ("@idm", SecondaryStaffIdm), ("@name", SecondaryStaffName), ("@number", "100002"));

            InsertCard(conn, NormalCardIdm, NormalCardType, NormalCardNumber, isLent: false, lentAt: null, lentStaff: null);
            InsertCard(conn, LentCardIdm, LentCardType, LentCardNumber, isLent: true, lentAt: now, lentStaff: SecondaryStaffIdm);
            InsertCard(conn, LowBalanceCardIdm, LowBalanceCardType, LowBalanceCardNumber, isLent: false, lentAt: null, lentStaff: null);

            // 通常カード: 当月の利用履歴（新規購入 → 利用 → チャージ → 利用）。残額は順に計算する。
            var balance = 0;
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 1), "新規購入", income: 10000, expense: 0, balance, PrimaryStaffName);
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 2), "鉄道（博多～天神）", 0, 260, balance, PrimaryStaffName);
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 2), "バス（博多駅前～天神）", 0, 230, balance, PrimaryStaffName);
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 5), "鉄道（博多～薬院 往復）", 0, 420, balance, SecondaryStaffName, SecondaryStaffIdm);
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 8), "役務費によりチャージ", 3000, 0, balance, PrimaryStaffName);
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 8), "鉄道（天神～福岡空港）", 0, 260, balance, PrimaryStaffName);
            balance = InsertLedger(conn, NormalCardIdm, DayOfMonth(today, 12), "鉄道（博多～貝塚）", 0, 340, balance, SecondaryStaffName, SecondaryStaffIdm);
            _ = balance;

            // 貸出中カード: 新規購入の後、貸出中レコード（is_lent_record=1）。
            // ic_card.is_lent と ledger の貸出中レコードは起動時の RepairLentStatusConsistencyAsync が突き合わせるため
            // 両方を揃えて投入する（片方だけだと起動時に修復され、貸出中の表示が消える）。
            var lentBalance = InsertLedger(conn, LentCardIdm, DayOfMonth(today, 1), "新規購入", 5000, 0, 0, PrimaryStaffName);
            Execute(conn,
                "INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name, note, lent_at, is_lent_record) " +
                "VALUES (@card, @lender, @date, @summary, 0, 0, @balance, @staff, NULL, @lentAt, 1)",
                ("@card", LentCardIdm), ("@lender", SecondaryStaffIdm), ("@date", ToText(now)), ("@summary", "（貸出中）"),
                ("@balance", lentBalance), ("@staff", SecondaryStaffName), ("@lentAt", ToText(now)));

            // 残額不足カード: 残額がしきい値（既定 10,000 円）以下。
            var low = InsertLedger(conn, LowBalanceCardIdm, DayOfMonth(today, 1), "新規購入", 2000, 0, 0, PrimaryStaffName);
            _ = InsertLedger(conn, LowBalanceCardIdm, DayOfMonth(today, 3), "鉄道（博多～天神 往復）", 0, 520, low, PrimaryStaffName);

            tx.Commit();
        }

        private static void InsertCard(SQLiteConnection conn, string idm, string type, string number, bool isLent, DateTime? lentAt, string? lentStaff)
        {
            Execute(conn,
                "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number, note, is_deleted, is_lent, last_lent_at, last_lent_staff) " +
                "VALUES (@idm, @type, @number, NULL, 0, @isLent, @lentAt, @lentStaff)",
                ("@idm", idm), ("@type", type), ("@number", number), ("@isLent", isLent ? 1 : 0),
                ("@lentAt", lentAt.HasValue ? ToText(lentAt.Value) : (object)DBNull.Value),
                ("@lentStaff", lentStaff ?? (object)DBNull.Value));
        }

        /// <summary>台帳 1 行を投入し、投入後の残額を返す。</summary>
        private static int InsertLedger(
            SQLiteConnection conn, string cardIdm, DateTime date, string summary, int income, int expense, int previousBalance,
            string staffName, string lenderIdm = AppFixture.SeededStaffIdm)
        {
            var balance = previousBalance + income - expense;
            Execute(conn,
                "INSERT INTO ledger (card_idm, lender_idm, date, summary, income, expense, balance, staff_name, note, is_lent_record) " +
                "VALUES (@card, @lender, @date, @summary, @income, @expense, @balance, @staff, NULL, 0)",
                ("@card", cardIdm), ("@lender", lenderIdm), ("@date", ToText(date)), ("@summary", summary),
                ("@income", income), ("@expense", expense), ("@balance", balance), ("@staff", staffName));
            return balance;
        }

        /// <summary>当月の <paramref name="day"/> 日。今日より後にならないよう今日で頭打ちにする（月初の実行でも当月に収まる）。</summary>
        private static DateTime DayOfMonth(DateTime today, int day) =>
            new DateTime(today.Year, today.Month, Math.Min(day, today.Day));

        private static string ToText(DateTime value) =>
            value.ToString(DateTimePattern, CultureInfo.InvariantCulture);

        private static void Execute(SQLiteConnection conn, string sql, params (string name, object value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }
            cmd.ExecuteNonQuery();
        }
    }
}
