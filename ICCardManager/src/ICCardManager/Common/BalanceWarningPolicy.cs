namespace ICCardManager.Common
{
    /// <summary>
    /// 残額警告のしきい値判定（Issue #1998）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>境界は「以下」（<c>&lt;=</c>）である。</b>しきい値ちょうどの残額も警告の対象に含める。
    /// 設定値の表（<c>04_機能設計書</c> §7.1「この金額<b>以下</b>で警告表示」）、画面設計書、
    /// 管理者マニュアル、Excel 出力の見出し（<c>残額不足（N円以下）</c>）がいずれも「以下」で書かれており、
    /// 過去に <c>DashboardService</c> と <c>WarningService</c> の食い違いを「以下」へ統一した経緯もある。
    /// </para>
    /// <para>
    /// <b>この判定を呼び出し元へ配らない。</b>Issue #1998 の時点で同じ比較が 4 か所
    /// （<c>LendingService</c> / <c>DashboardService</c> / <c>AdminDashboardService</c> /
    /// <c>WarningService</c>）に書かれており、<c>LendingService</c> の 1 か所だけが厳密な
    /// <c>&lt;</c> のまま取り残されていた。結果、残額がちょうどしきい値のカードを返却すると
    /// <b>返却トーストは警告を出さないのに、直後のダッシュボード更新と警告一覧は同じカードを
    /// 残額不足として表示する</b>という、同じ操作の直後に矛盾した表示が並ぶ状態になっていた。
    /// 同じ判断を複数箇所へ書き直させない（<c>.claude/rules/db-write-conventions.md</c> #1763、
    /// <c>IcCard.IsInOperation</c> へ寄せた #1947 と同じ形）。
    /// </para>
    /// <para>
    /// しきい値は交通系ICカードに固有の概念ではない（「補充が必要な物品を見つける」判定であり、
    /// 交通系語彙で分岐しない）ため、汎用コアである <c>Common</c> に置く
    /// （<c>.claude/rules/domain-boundaries.md</c> の決定木①）。
    /// </para>
    /// <para>
    /// <b>回帰の担保は 2 段構えで、静的検査は網羅ではない。</b>
    /// <c>BalanceWarningComparisonConventionTests</c> の直書き検査は「しきい値の識別子が比較演算子に
    /// 隣接する形」を見るので、<b>別名のローカルへ退避してから比較する形</b>
    /// （<c>var t = settings.WarningBalance; … balance &lt;= t</c>）や
    /// <c>balance.CompareTo(settings.WarningBalance) &lt;= 0</c> は原理的に拾えない。
    /// これらは同テストの「残額警告フラグへの代入がすべて共通の判定を右辺に持つこと」が塞ぐ
    /// （比較結果を <c>IsLowBalance</c> / <c>IsBalanceWarning</c> へ入れる限り検出される）。
    /// <b>どちらにも掛からない新しい消費側を作るときは、この節を読んで判定をここへ委譲すること。</b>
    /// </para>
    /// </remarks>
    internal static class BalanceWarningPolicy
    {
        /// <summary>
        /// 残額が警告しきい値に達しているか（＝補充を促すべきか）を返す。
        /// </summary>
        /// <param name="balance">カードの残額（円）。</param>
        /// <param name="warningBalance">残額警告しきい値（円。<c>AppSettings.WarningBalance</c>）。</param>
        /// <returns>残額がしきい値<b>以下</b>のとき true。</returns>
        internal static bool IsLowBalance(int balance, int warningBalance)
        {
            return balance <= warningBalance;
        }

        /// <summary>
        /// 残額不足の見出しに境界を添えた表記を組み立てる。
        /// </summary>
        /// <param name="warningBalance">残額警告しきい値（円。<c>AppSettings.WarningBalance</c>）。</param>
        /// <returns><c>残額不足（10,000円以下）</c>。</returns>
        /// <remarks>
        /// <para>
        /// <b>核（見出し＋境界）と装飾（トーストの <c>⚠️ </c>）を分ける（Issue #2077）。</b>
        /// 境界を述べる表記は返却トースト（<see cref="FormatLowBalanceNotice"/>）だけでなく
        /// 管理者ダッシュボードの Excel 出力（<c>AdminDashboardExcelExportService</c> の集計見出し）にもあり、
        /// 装飾を焼き込んだ文言しか無いと後者から再利用できない。再利用できない形にすると、
        /// 「境界を動かすときに判定と表記の両方が視野に入る」という本クラスの目的が
        /// <b>消費側の数だけ半分ずつ失われる</b>（#1763「同じ判断を配らない」。コードレビューで検出）。
        /// </para>
        /// <para>
        /// 装飾を付けるかどうかは表示先で決める。Excel のセルは見出しの列であり、
        /// 1 行ごとに警告記号を並べる場所ではない。
        /// </para>
        /// </remarks>
        internal static string FormatLowBalanceThresholdLabel(int warningBalance)
        {
            return $"残額不足（{DisplayFormatters.FormatBalanceWithUnit(warningBalance)}以下）";
        }

        /// <summary>
        /// 返却トーストに出す残額警告の文言を組み立てる。
        /// </summary>
        /// <param name="warningBalance">残額警告しきい値（円。<c>AppSettings.WarningBalance</c>）。</param>
        /// <returns><c>⚠️ 残額不足（10,000円以下）</c> のような 1 行の文言。</returns>
        /// <remarks>
        /// <para>
        /// <b>境界の表記は判定と同じ場所に置く（Issue #2077）。</b>従来この文言は
        /// <c>ToastNotificationWindow.ShowReturn</c> が <c>$"⚠️ 残額不足（&lt;{warningBalance:N0}円）"</c> と
        /// 直接組み立てており、#1998 で判定を「以下」へ統一した後も<b>表記だけが「未満」のまま</b>残っていた。
        /// 残額がちょうど 10,000 円のカードを返却すると「残額不足（&lt;10,000円）」と出る
        /// ——警告は正しいのに、その理由として述べている条件が事実と合わない。チャージは千円単位で
        /// 行われるため、しきい値ちょうどの残額は日常的に発生する。
        /// </para>
        /// <para>
        /// 判定（<see cref="IsLowBalance"/>）と表記を同じクラスに置くと、境界を動かすときに
        /// 両方が視野に入る。片方だけを直せる形にしないこと
        /// （<c>.claude/rules/db-write-conventions.md</c> #1763「同じ判断を配らない」）。
        /// </para>
        /// <para>
        /// <b>短さは仕様である。</b>トーストは文字サイズ「大/特大」で折り返しが増えるため、
        /// #1273 で「残額が少なくなっています（しきい値: 10,000円）」（約 26 文字）から
        /// 現在の形（約 16 文字）へ短縮した経緯がある。語彙は Excel 出力の見出し
        /// （<c>残額不足（N円以下）</c>）・管理者マニュアルと揃えており、
        /// <c>≦</c> のような記号ではなく「以下」と書くのは、読み手が庶務担当者だからである。
        /// </para>
        /// </remarks>
        internal static string FormatLowBalanceNotice(int warningBalance)
        {
            return $"⚠️ {FormatLowBalanceThresholdLabel(warningBalance)}";
        }
    }
}
