using System.Collections.Generic;

namespace ICCardManager.Services
{
    /// <summary>
    /// 組織固有の設定オプション（Issue #974）
    /// </summary>
    /// <remarks>
    /// <para>
    /// appsettings.json の "OrganizationOptions" セクションにバインドされます。
    /// セクションが存在しない場合は、全てのデフォルト値が適用され、
    /// 既存の動作（福岡市向け）と完全に一致します。
    /// </para>
    /// <para>
    /// 他の組織で利用する場合は、appsettings.json に OrganizationOptions セクションを
    /// 追加して、必要な項目のみ上書きしてください。
    /// </para>
    /// </remarks>
    public class OrganizationOptions
    {
        /// <summary>
        /// 摘要テキスト設定
        /// </summary>
        public SummaryTextOptions SummaryText { get; set; } = new();

        /// <summary>
        /// 摘要生成ルール設定
        /// </summary>
        public SummaryRulesOptions SummaryRules { get; set; } = new();

        /// <summary>
        /// 駅コードエリア優先順位設定
        /// </summary>
        public AreaPriorityOptions AreaPriority { get; set; } = new();

        /// <summary>
        /// 帳票レイアウト設定
        /// </summary>
        public ReportLayoutOptions ReportLayout { get; set; } = new();

        /// <summary>
        /// テンプレート列マッピング設定
        /// </summary>
        public TemplateMappingOptions TemplateMapping { get; set; } = new();
    }

    /// <summary>
    /// 摘要テキストの設定
    /// </summary>
    /// <remarks>
    /// 物品出納簿に出力される各種テキストを組織に合わせてカスタマイズできます。
    /// Format系プロパティでは {0}, {1} 等のプレースホルダが使用されます。
    /// </remarks>
    public class SummaryTextOptions
    {
        /// <summary>
        /// 市長事務部局のチャージ摘要
        /// </summary>
        public string ChargeSummaryMayorOffice { get; set; } = "役務費によりチャージ";

        /// <summary>
        /// 企業会計部局のチャージ摘要
        /// </summary>
        public string ChargeSummaryEnterprise { get; set; } = "旅費によりチャージ";

        /// <summary>
        /// ポイント還元の摘要
        /// </summary>
        public string PointRedemption { get; set; } = "ポイント還元";

        /// <summary>
        /// 払い戻しの摘要
        /// </summary>
        public string RefundSummary { get; set; } = "払戻しによる払出";

        /// <summary>
        /// 区間を特定できない利用の代替摘要（Issue #1735）
        /// </summary>
        /// <remarks>
        /// 乗車駅・降車駅の両方が欠落し摘要の自動生成が空文字になった利用レコードに充てる文言。
        /// 摘要が空欄の台帳行を保存しないための安全網。
        /// </remarks>
        public string UnknownUsageSummary { get; set; } = "鉄道（区間不明）";

        /// <summary>
        /// 貸出中の摘要
        /// </summary>
        public string LendingSummary { get; set; } = "（貸出中）";

        /// <summary>
        /// 前年度繰越の摘要
        /// </summary>
        public string CarryoverFromPreviousYear { get; set; } = "前年度より繰越";

        /// <summary>
        /// 次年度繰越の摘要
        /// </summary>
        public string CarryoverToNextYear { get; set; } = "次年度へ繰越";

        /// <summary>
        /// 前月繰越の摘要フォーマット（{0}=月番号）
        /// </summary>
        public string CarryoverFromMonthFormat { get; set; } = "{0}月より繰越";

        /// <summary>
        /// 年度途中導入の繰越摘要フォーマット（{0}=月番号）
        /// </summary>
        public string MidYearCarryoverFormat { get; set; } = "{0}月から繰越";

        /// <summary>
        /// 年度途中導入の繰越パターン判定用正規表現
        /// </summary>
        public string MidYearCarryoverPattern { get; set; } = @"^(1[0-2]|[1-9])月から繰越$";

        /// <summary>
        /// 月計の摘要フォーマット（{0}=月番号）
        /// </summary>
        public string MonthlySummaryFormat { get; set; } = "{0}月計";

        /// <summary>
        /// 累計の摘要
        /// </summary>
        public string CumulativeSummary { get; set; } = "累計";

        /// <summary>
        /// 残高不足時の備考テキストフォーマット（{0}=支払総額、{1}=不足額）
        /// </summary>
        public string InsufficientBalanceNoteFormat { get; set; } = "支払額{0}円のうち不足額{1}円は現金で支払（旅費支給）";

        /// <summary>
        /// 鉄道利用のラベル
        /// </summary>
        public string RailwayLabel { get; set; } = "鉄道";

        /// <summary>
        /// バス利用のラベル
        /// </summary>
        public string BusLabel { get; set; } = "バス";

        /// <summary>
        /// バス停名未入力時のプレースホルダ
        /// </summary>
        public string BusPlaceholder { get; set; } = "★";

        /// <summary>
        /// 往復の接尾辞
        /// </summary>
        public string RoundTripSuffix { get; set; } = " 往復";
    }

    /// <summary>
    /// 摘要生成ルールの設定
    /// </summary>
    /// <remarks>
    /// 組織により「どこまで摘要をまとめるか」が異なるため、
    /// 各ルールを個別にON/OFFできます。
    /// </remarks>
    public class SummaryRulesOptions
    {
        /// <summary>
        /// 往復検出を有効にするか（A→B + B→A → 「A～B 往復」）
        /// </summary>
        public bool EnableRoundTripDetection { get; set; } = true;

        /// <summary>
        /// 乗継統合を有効にするか（A→B + B→C → 「A～C」）
        /// </summary>
        public bool EnableTransferConsolidation { get; set; } = true;

        /// <summary>
        /// 乗り継ぎ駅として同一視するグループ
        /// </summary>
        /// <remarks>
        /// 異なる事業者間で駅名が異なるが、物理的に近接する駅のグループ。
        /// デフォルトは福岡地区の乗り継ぎ駅グループ。
        /// </remarks>
        public List<List<string>> TransferStationGroups { get; set; } = new()
        {
            new List<string> { "天神", "西鉄福岡(天神)" },
            new List<string> { "千早", "西鉄千早" }
        };
    }

    /// <summary>
    /// 駅コードエリア優先順位の設定
    /// </summary>
    /// <remarks>
    /// カード種別ごとに、駅コード検索時のエリア優先順位を指定します。
    /// エリアコード: 0=JR東日本・関東圏, 1=JR西日本・関西圏, 2=JR東海・中部圏, 3=JR九州・九州圏
    /// </remarks>
    public class AreaPriorityOptions
    {
        /// <summary>
        /// デフォルトの優先順位（カード種別指定なし、または未知のカード種別の場合）
        /// </summary>
        public int[] DefaultPriority { get; set; } = { 3, 0, 1, 2 };

        /// <summary>
        /// カード種別ごとの優先順位（キーはCardType列挙値の名前）
        /// </summary>
        /// <remarks>
        /// 未指定のカード種別にはDefaultPriorityが適用されます。
        /// </remarks>
        public Dictionary<string, int[]> CardTypePriorities { get; set; } = new();
    }

    /// <summary>
    /// 帳票レイアウトの設定
    /// </summary>
    public class ReportLayoutOptions
    {
        /// <summary>
        /// 帳票タイトル
        /// </summary>
        public string TitleText { get; set; } = "物品出納簿";

        /// <summary>
        /// 物品分類の表示テキスト
        /// </summary>
        public string ClassificationText { get; set; } = "雑品（金券類）";

        /// <summary>
        /// 単位の表示テキスト
        /// </summary>
        public string UnitText { get; set; } = "円";

        /// <summary>
        /// ファイル名フォーマット（{0}=カード種別、{1}=カード番号、{2}=年度）
        /// </summary>
        public string FileNameFormat { get; set; } = "物品出納簿_{0}_{1}_{2}年度.xlsx";
    }

    /// <summary>
    /// テンプレート列マッピングの設定
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excel テンプレートの<b>ヘッダー行（2行目）</b>のセル配置を、組織独自のテンプレートに
    /// 合わせてカスタマイズできます。<see cref="ReportService"/> の <c>SetHeaderInfo</c> が
    /// これらの列番号を読みます。
    /// </para>
    /// <para>
    /// <b>明細行（出納年月日・摘要・受入・払出・残額・氏名・備考）の配置はここでは変更できません。</b>
    /// 明細行のレイアウトは列番号だけでは決まらず、結合セル（摘要 B～D 列・備考 I～L 列）・
    /// 罫線・改ページ位置（1ページ＝22行、ヘッダー1-4行／データ5-16行／備考17-22行）と
    /// 一体でテンプレートファイル自体に埋め込まれているためです。
    /// </para>
    /// <para>
    /// Issue #1820: 以前は <c>DataStartRow</c> / <c>RowsPerPage</c> / <c>DateColumn</c> 等
    /// 13 項目が定義されていましたが、<b>本番コードから一度も読まれていませんでした</b>
    /// （<c>ReportService</c> は同名のローカル <c>const</c> と列リテラルを使っていた）。
    /// 管理者マニュアル §7.4 が設定可能と案内していたため、設定しても反映されないという
    /// 状態になっていたので削除しました。明細行の配置を可変にする要件が出た場合は、
    /// <c>ExcelStyleFormatter</c> の結合・罫線・印刷範囲まで含めて設計し直したうえで
    /// 追加してください（使われない設定項目を先に作らない）。
    /// </para>
    /// </remarks>
    public class TemplateMappingOptions
    {
        /// <summary>
        /// カード種別の列番号（ヘッダー内）
        /// </summary>
        public int CardTypeColumn { get; set; } = 5;

        /// <summary>
        /// カード番号の列番号（ヘッダー内）
        /// </summary>
        public int CardNumberColumn { get; set; } = 8;

        /// <summary>
        /// 単位の列番号（ヘッダー内）
        /// </summary>
        public int UnitColumn { get; set; } = 10;

        /// <summary>
        /// 分類テキストの列番号（ヘッダー内）
        /// </summary>
        public int ClassificationColumn { get; set; } = 2;

        /// <summary>
        /// ページ番号の列番号（ヘッダー内）
        /// </summary>
        public int PageNumberColumn { get; set; } = 12;
    }
}
