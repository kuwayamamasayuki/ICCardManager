using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1468: スクリーンリーダー対応の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 対象ダイアログでアクセシビリティ関連属性（<c>AutomationProperties.Name</c> /
/// <c>AutomationProperties.HelpText</c> / <c>AutomationProperties.LiveSetting</c>）の
/// 付与数が、現時点の水準を下回らないことを静的解析で検証する。
/// </para>
/// <para>
/// 動的に <c>Text</c> が書き換わる <c>TextBlock</c> では <c>AutomationProperties.Name</c> を
/// 設定すると Text のフォールバック読み上げが上書きされてしまうため、
/// <c>HelpText</c> + <c>LiveSetting</c> の組み合わせを採用している。
/// </para>
/// <para>
/// Issue #2102: 照合は XML コメントを除去してから、**要素の開始タグ単位**で行う。
/// ファイル全体の正規表現で数えていた頃は、StaffAuthDialog の Name 5 件のうち 1 件が
/// 「Name を設定しない」と説明するコメントの中の字句で、下限も 4 件だったため、
/// 実際の属性を 1 つ消しても緑だった（#1692 の極性の反転）。C# 側もコメントを除去してから
/// 呼び出しの存在を表明する（<see cref="TestSourceInspection"/>）。
/// </para>
/// <para>
/// 実際にスクリーンリーダー（NVDA / Narrator）で読み上げられるかは UI 自動化を要し、
/// PR テストプランで手動検証する。本テストは XAML 上の付与漏れを早期検出するための
/// 静的セーフティネット。
/// </para>
/// </remarks>
public class DialogAutomationPropertiesCoverageTests
{
    private const string AutomationName = "AutomationProperties.Name";
    private const string AutomationHelpText = "AutomationProperties.HelpText";
    private const string AutomationLiveSetting = "AutomationProperties.LiveSetting";

    /// <summary>
    /// 各ダイアログにおける <c>AutomationProperties.Name</c> を持つ要素の最低数。
    /// 値は Issue #2102 時点の**実数**（コメントを除いて数えた値）に合わせてあり、1 つでも消すと赤になる。
    /// 値を増やす変更は許可（カバレッジ向上）、減らす変更は要レビュー（カバレッジ後退）。
    /// </summary>
    public static TheoryData<string, int> MinimumNameCounts => new()
    {
        // OperationLogDialog: Window + 検索条件入力6個（開始日/終了日/操作種別/対象テーブル/対象ID/操作者名）+
        // 期間クイック3個（今日/今月/先月）+ 検索/クリア2個 + DataGrid + ページサイズ +
        // ページネーション4個（最初/前/次/最後）+ エクスポート2個（実行/開く）+ 閉じる + 処理中2個（オーバーレイ/テキスト）= 23個
        // （Issue #1502: 実 XAML の付与数 23 と一致させ、回帰防止の感度を最大化）
        { "OperationLogDialog.xaml", 23 },
        // StaffAuthDialog: Window + 認証アイコン + キャンセル + 職員証仮想タッチ = 4個
        // （Issue #2102: 旧実装はコメント中の「AutomationProperties.Name を設定しない」も数えて 5 件としていた）
        { "StaffAuthDialog.xaml", 4 },
    };

    /// <summary>
    /// 各ダイアログにおける <c>AutomationProperties.HelpText</c> を持つ要素の最低数（Issue #2102 時点の実数）。
    /// </summary>
    public static TheoryData<string, int> MinimumHelpTextCounts => new()
    {
        { "OperationLogDialog.xaml", 27 },
        { "StaffAuthDialog.xaml", 7 },
    };

    [Theory]
    [MemberData(nameof(MinimumNameCounts))]
    public void Dialog_should_meet_minimum_AutomationProperties_Name_coverage(string xamlFileName, int minimumCount)
    {
        var actualCount = CountStartTagsWithAttribute(ReadDialog(xamlFileName), AutomationName);

        actualCount.Should().BeGreaterThanOrEqualTo(minimumCount,
            $"{xamlFileName}: AutomationProperties.Name を持つ要素が {actualCount} 個で、" +
            $"最低水準 {minimumCount} を下回っている。" +
            "スクリーンリーダー利用ユーザーがコントロールを識別できなくなるおそれがあるため、" +
            "削除・統合した場合は MinimumNameCounts の値を見直すか、別のコントロールに付与し直すこと。");
    }

    [Theory]
    [MemberData(nameof(MinimumHelpTextCounts))]
    public void Dialog_should_meet_minimum_AutomationProperties_HelpText_coverage(string xamlFileName, int minimumCount)
    {
        var actualCount = CountStartTagsWithAttribute(ReadDialog(xamlFileName), AutomationHelpText);

        actualCount.Should().BeGreaterThanOrEqualTo(minimumCount,
            $"{xamlFileName}: AutomationProperties.HelpText を持つ要素が {actualCount} 個で、" +
            $"最低水準 {minimumCount} を下回っている。" +
            "操作のヒント情報を補強できなくなるため、HelpText を削減する場合は閾値を見直すこと。");
    }

    /// <summary>
    /// Issue #2102: 付与数の数え方（コメントを除き、開始タグの属性だけを数える）を合成入力で固定する。
    /// </summary>
    [Theory]
    // コメントの中の字句は数えない（旧実装は数えていた）
    [InlineData("<!-- AutomationProperties.Name を設定しない -->\n<Button AutomationProperties.Name=\"a\"/>", 1)]
    // 属性値の中の字句は数えない
    [InlineData("<TextBlock Text=\"AutomationProperties.Name を参照\" AutomationProperties.HelpText=\"b\"/>", 0)]
    // 属性名が前方一致するだけのもの（NameProperty 等）は数えない
    [InlineData("<Button AutomationProperties.NameX=\"a\"/>", 0)]
    // 複数の要素・単引用符・改行を挟んだ属性
    [InlineData("<Button AutomationProperties.Name='a'/>\n<TextBox\n    AutomationProperties.Name = \"b\"/>", 2)]
    public void 付与数はコメントを除いた開始タグの属性だけを数えること(string syntheticXaml, int expectedCount)
    {
        CountStartTagsWithAttribute(syntheticXaml, AutomationName).Should().Be(expectedCount,
            $"入力: {syntheticXaml.Replace("\n", "\\n")}");
    }

    /// <summary>
    /// OperationLogDialog の主要操作要素は AutomationProperties.Name で個別に識別できなければならない。
    /// </summary>
    [Theory]
    [InlineData("検索を実行")]
    [InlineData("検索条件をクリア")]
    [InlineData("対象ID")]
    [InlineData("操作者名")]
    [InlineData("操作種別")]
    [InlineData("対象テーブル")]
    [InlineData("Excelファイルにエクスポート")]
    [InlineData("最初のページへ移動")]
    [InlineData("最後のページへ移動")]
    [InlineData("1ページあたりの表示件数")]
    public void OperationLogDialog_should_label_key_controls_for_screen_readers(string requiredName)
    {
        var xaml = ReadDialog("OperationLogDialog.xaml");

        HasElementWithAutomationName(xaml, requiredName).Should().BeTrue(
            $"OperationLogDialog: 主要コントロールに AutomationProperties.Name=\"{requiredName}\" が必要。" +
            "Issue #1468 で業務監査画面（操作ログ）のスクリーンリーダー対応を改善した際の付与項目。");
    }

    /// <summary>
    /// Issue #1504: <see cref="HasElementWithAutomationName"/> が空白挿入を許容しつつ
    /// 値の厳密一致を維持していることを、合成 XAML サンプルで固定する回帰テスト。
    /// </summary>
    /// <remarks>
    /// Issue #2102 でコメント中の字句を拾わないことも併せて固定した。
    /// </remarks>
    [Theory]
    [InlineData("<Button AutomationProperties.Name=\"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button AutomationProperties.Name = \"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button AutomationProperties.Name  =  \"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button\n    AutomationProperties.Name=\"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button AutomationProperties.Name=\"別の語\" />", "検索を実行", false)]
    [InlineData("<Button AutomationProperties.Name=\"検索\" />", "検索を実行", false)]
    [InlineData("<!-- <Button AutomationProperties.Name=\"検索を実行\" /> -->", "検索を実行", false)]
    [InlineData("", "検索を実行", false)]
    public void AutomationNamePattern_should_be_whitespace_tolerant_but_value_strict(
        string syntheticXaml, string requiredName, bool expectedMatch)
    {
        HasElementWithAutomationName(syntheticXaml, requiredName).Should().Be(expectedMatch,
            $"requiredName='{requiredName}' に対する判定は、空白の有無や改行を許容しつつ" +
            "値の部分一致や別文字列を取り違えてはならない（Issue #1504）。" +
            $"入力: {syntheticXaml.Replace("\n", "\\n")}");
    }

    /// <summary>
    /// StaffAuthDialog（職員証認証）は最重要のダイアログであり、ステータス変化と
    /// タイムアウト残時間がスクリーンリーダーに通知される必要がある。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_status_text_should_have_assertive_live_setting()
    {
        var tag = FindSingleStartTag(ReadDialog("StaffAuthDialog.xaml"), "x:Name", "StatusText");

        XamlElementInspection.GetAttribute(tag, AutomationLiveSetting).Should().Be("Assertive",
            "StaffAuthDialog: 認証ステータスは Assertive な LiveSetting で即時通知すべき。" +
            "認証成功・失敗の結果がスクリーンリーダー利用者に伝わらないと、誤操作の温床になる。" +
            "（Issue #1503: 同一開始タグ内に限定して判定する）");
    }

    [Fact]
    public void StaffAuthDialog_timeout_text_should_have_polite_live_setting()
    {
        var tag = FindSingleStartTag(ReadDialog("StaffAuthDialog.xaml"), "x:Name", "TimeoutText");

        XamlElementInspection.GetAttribute(tag, AutomationLiveSetting).Should().Be("Polite",
            "StaffAuthDialog: タイムアウト残り時間は Polite で通知し、" +
            "頻繁な秒数更新がスクリーンリーダーの読み上げを阻害しないようにする。" +
            "（Issue #1503: 同一開始タグ内に限定して判定する）");
    }

    /// <summary>
    /// OperationLogDialog の検索ステータスメッセージは LiveSetting で変化を通知する。
    /// </summary>
    [Fact]
    public void OperationLogDialog_status_message_should_announce_changes()
    {
        HasStatusMessageWithLiveSetting(ReadDialog("OperationLogDialog.xaml"), "Polite").Should().BeTrue(
            "OperationLogDialog: ステータスメッセージは Polite な LiveSetting で検索結果や件数の変化を通知すべき。" +
            "（Issue #1503: 同一開始タグ内に限定して判定する）");
    }

    /// <summary>
    /// Issue #1503: LiveSetting の判定は同一要素内（同一開始タグ内）に閉じ込められるべき。
    /// </summary>
    /// <remarks>
    /// Issue #2102 で正規表現から開始タグ単位の判定（<see cref="XamlElementInspection"/>）へ移した。
    /// 合成入力は旧来の正規表現を固定していたものをそのまま使い、同じ意味論を保っていることを表明する。
    /// </remarks>
    [Theory]
    // 同一要素内（マッチすべき）
    [InlineData(
        "<TextBlock Text=\"{Binding StatusMessage}\" AutomationProperties.LiveSetting=\"Polite\"/>",
        true)]
    // マルチライン属性レイアウト（マッチすべき）
    [InlineData(
        "<TextBlock Text=\"{Binding StatusMessage}\"\n           AutomationProperties.LiveSetting=\"Polite\"/>",
        true)]
    // 別要素を跨ぐ（マッチしてはならない: 旧 [\s\S]*? regex なら誤検出）
    [InlineData(
        "<TextBlock Text=\"{Binding StatusMessage}\"/><TextBlock AutomationProperties.LiveSetting=\"Polite\"/>",
        false)]
    // Text= と LiveSetting= が別 TextBlock に分かれているケース（マッチしてはならない）
    [InlineData(
        "<TextBlock Text=\"{Binding StatusMessage}\" Foreground=\"Red\"/>\n<TextBlock AutomationProperties.LiveSetting=\"Polite\"/>",
        false)]
    // 同じ要素内に LiveSetting がない（マッチしてはならない）
    [InlineData(
        "<TextBlock Text=\"{Binding StatusMessage}\"/>",
        false)]
    public void LiveSettingPattern_should_be_scoped_to_same_element(string syntheticXaml, bool expectedMatch)
    {
        HasStatusMessageWithLiveSetting(syntheticXaml, "Polite").Should().Be(expectedMatch,
            "Issue #1503: LiveSetting の判定は同一開始タグ内に閉じ込めるべき。" +
            $"入力: {syntheticXaml.Replace("\n", "\\n")}");
    }

    /// <summary>
    /// Issue #1503: 旧 <c>[\s\S]*?</c> パターンが要素境界を跨ぐ誤マッチを起こしていた事実を
    /// 記録する回帰テスト。「なぜ同一開始タグ内で判定するか」のドキュメントを兼ねる。
    /// </summary>
    [Fact]
    public void OldGreedyPattern_would_have_falsely_matched_cross_element_LiveSetting()
    {
        // StatusMessage の TextBlock と、別 TextBlock の LiveSetting="Polite" が並ぶケース。
        // この XAML から「StatusMessage の TextBlock に LiveSetting が付いている」と
        // 推論するのは誤り。旧 regex はこの誤推論を許していた。
        var crossElementXaml =
            "<TextBlock Text=\"{Binding StatusMessage}\"/>" +
            "<TextBlock AutomationProperties.LiveSetting=\"Polite\"/>";

        var oldUnsafePattern = @"Text=""\{Binding StatusMessage\}""[\s\S]*?AutomationProperties\.LiveSetting=""Polite""";

        Regex.IsMatch(crossElementXaml, oldUnsafePattern).Should().BeTrue(
            "Issue #1503 (回帰記録): 旧 `[\\s\\S]*?` regex は要素境界を考慮せず、" +
            "別 TextBlock の LiveSetting=\"Polite\" を誤検出していた。" +
            "本アサートは旧挙動を固定する記録目的。");

        HasStatusMessageWithLiveSetting(crossElementXaml, "Polite").Should().BeFalse(
            "現行の判定は同一開始タグ内に限定されるため、" +
            "別 TextBlock の LiveSetting=\"Polite\" を誤検出しない。");
    }

    /// <summary>
    /// 動的更新される TextBlock では AutomationProperties.Name を付けないこと。
    /// Name を付けると Text のフォールバック読み上げが上書きされ、内容変化が伝わらなくなる。
    /// </summary>
    /// <remarks>
    /// Issue #1501: OperationLogDialog 側の動的 TextBlock 4 要素（ページ情報・現在ページ番号・
    /// 検索ステータスメッセージ・処理中メッセージ）を追加。PR #1500 の `CHANGELOG.md` および
    /// `docs/design/03_画面設計書.md` で対象として明示されていたが回帰テストに反映されていなかった。
    /// <para>
    /// Issue #2073: 本テストは <c>x:Name</c> を持つ TextBlock を**個別に列挙**するため、
    /// <c>Text="{Binding …}"</c> だけの TextBlock（Views 配下に 16 箇所あった）を検出できない。
    /// 走査範囲を導出する横断ガードは
    /// <see cref="ICCardManager.Tests.Views.DynamicTextAutomationNameConventionTests"/> が担う。
    /// 本テストは「この 7 要素が今後も対象であり続けること」を名指しで固定する役に留める。
    /// </para>
    /// <para>
    /// Issue #2102: 旧実装の <c>[^/]*?</c> は開始タグを越えて後続の要素の Name に一致し得た。
    /// 要素を 1 つに絞ってから属性を調べる。要素が実在することも併せて表明する（改名で空振りしない）。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("StaffAuthDialog.xaml", "OperationDescriptionText")]
    [InlineData("StaffAuthDialog.xaml", "StatusText")]
    [InlineData("StaffAuthDialog.xaml", "TimeoutText")]
    [InlineData("OperationLogDialog.xaml", "PageInfoText")]
    [InlineData("OperationLogDialog.xaml", "CurrentPageNumberText")]
    [InlineData("OperationLogDialog.xaml", "StatusMessageText")]
    [InlineData("OperationLogDialog.xaml", "ProcessingOverlayText")]
    public void Dynamic_text_blocks_should_not_have_AutomationProperties_Name(string xamlFileName, string elementName)
    {
        var tag = FindSingleStartTag(ReadDialog(xamlFileName), "x:Name", elementName);

        tag.Should().StartWith("<TextBlock", $"{xamlFileName}: x:Name=\"{elementName}\" は TextBlock のはず");
        XamlElementInspection.GetAttribute(tag, AutomationName).Should().BeNull(
            $"{xamlFileName}: x:Name=\"{elementName}\" は動的に Text が更新される TextBlock。" +
            "AutomationProperties.Name を付けると Text の読み上げが上書きされてしまうため、" +
            "HelpText と LiveSetting のみを使い、Text 自体を読み上げ対象にすること。");
    }

    /// <summary>
    /// Issue #1509: StatusBorder の初期 Visibility が Collapsed であってはならない。
    /// Collapsed → Visible 遷移は WPF UI Automation の LiveRegionChanged を発火させないため、
    /// 常時可視化して AutomationTree 上に常駐させる必要がある。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_StatusBorder_should_not_be_initially_collapsed()
    {
        var tag = FindSingleStartTag(ReadDialog("StaffAuthDialog.xaml"), "x:Name", "StatusBorder");

        XamlElementInspection.GetAttribute(tag, "Visibility").Should().NotBe("Collapsed",
            "StaffAuthDialog: StatusBorder の初期 Visibility が Collapsed だと " +
            "AutomationTree から除外され、Text 更新時に LiveRegionChanged が発火しない（Issue #1509）。" +
            "Background=\"Transparent\" + BorderThickness=\"0\" で常時可視化すること。");
    }

    /// <summary>
    /// Issue #1509: ShowStatus 内で UIElementAutomationPeer.RaiseAutomationEvent を
    /// 明示呼び出ししないと、Text 代入だけでは LiveRegionChanged が確実に発火しない。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_code_behind_should_raise_LiveRegionChanged()
    {
        var showStatusBody = TestSourceInspection.ExtractMethodBody(
            ReadCodeOnly("StaffAuthDialog.xaml.cs"), "private void ShowStatus(");

        showStatusBody.Should().MatchRegex(@"RaiseAutomationEvent\s*\(\s*AutomationEvents\.LiveRegionChanged\s*\)",
            "StaffAuthDialog: Text 更新だけでは LiveRegionChanged が確実に発火しないため、" +
            "ShowStatus 内で UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) を明示呼び出しすること（Issue #1509）。" +
            "コメントの中の記述は数えない（Issue #2102）。");
    }

    /// <summary>
    /// Issue #1509: 認証成功時にも ShowStatus でステータス表示し、
    /// スクリーンリーダーに認証成功を通知する。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_authentication_success_path_should_call_ShowStatus()
    {
        // 文字列リテラルで成功パスを識別するため、リテラルは残してコメントだけを除く
        var code = TestSourceInspection.RemoveCommentsPreservingLines(ReadCodeBehind("StaffAuthDialog.xaml.cs"));

        Regex.IsMatch(code, @"ShowStatus\(\$?""認証に成功")
            .Should().BeTrue(
            "StaffAuthDialog: 認証成功時にも ShowStatus(\"認証に成功しました...\") を呼び出して " +
            "スクリーンリーダー利用者に成功を通知すること（Issue #1509）。");
    }

    /// <summary>
    /// Issue #1509/Issue #1392: ShowStatus メソッド本体に色値リテラル（#RRGGBB）を
    /// 直接記述してはならない。AccessibilityStyles.xaml のブラシキーを
    /// DynamicResource / FindResource 経由で参照すること。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_ShowStatus_should_use_DynamicResource_for_colors()
    {
        // Issue #2102: 私的な波括弧スキャナを TestSourceInspection へ寄せた（コメント・リテラル内の波括弧で範囲が狂わない）
        var showStatusBody = TestSourceInspection.ExtractMethodBody(
            ReadCodeOnly("StaffAuthDialog.xaml.cs"), "private void ShowStatus(");

        // 妥当性チェック: 抽出範囲が ShowStatus の本体であること（必ず StatusText へ書き込む）
        showStatusBody.Should().Contain("StatusText",
            "ExtractMethodBody が ShowStatus の本体を正しく抽出できていない可能性。");

        Regex.IsMatch(showStatusBody, @"0x[0-9A-Fa-f]{2}")
            .Should().BeFalse(
            "StaffAuthDialog.ShowStatus: 色値リテラル（0xFF, 0xEB 等）は AccessibilityStyles.xaml の " +
            "ブラシキー（ErrorBackgroundBrush / SuccessBackgroundBrush 等）を FindResource 経由で参照すること（Issue #1392）。" +
            "現在のメソッド本体: " + showStatusBody);
    }

    /// <summary>
    /// Issue #1548: OperationLogDialog のコードビハインドが LiveRegionChanged を明示発火していること。
    /// AutomationProperties.LiveSetting="Polite" 単独ではスクリーンリーダーが沈黙するため、
    /// UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) の呼び出しが必須。
    /// </summary>
    /// <remarks>
    /// Issue #2102: 旧実装はファイルに <c>RaiseAutomationEvent</c> の字句があるかだけを見ていたため、
    /// 発火メソッドの定義を残したまま**呼び出し**（<c>OnViewModelPropertyChanged</c> 内の
    /// <c>RaiseLiveRegionChanged(target);</c>）を消しても緑だった。通知の経路を
    /// 「PropertyChanged の受け手が発火メソッドを呼ぶ」「発火メソッドが LiveRegionChanged を上げる」の 2 段で表明する。
    /// </remarks>
    [Fact]
    public void OperationLogDialog_CodeBehindに_RaiseAutomationEventLiveRegionChangedが存在すること()
    {
        var code = ReadCodeOnly("OperationLogDialog.xaml.cs");

        TestSourceInspection.ExtractMethodBody(code, "private void OnViewModelPropertyChanged(")
            .Should().MatchRegex(@"\bRaiseLiveRegionChanged\s*\(",
                "ViewModel の PropertyChanged の受け手が、対応する TextBlock について発火メソッドを呼び出していること。" +
                "呼び出しが無いと、発火メソッドが定義されていても読み上げは起きない（Issue #2102）。");

        TestSourceInspection.ExtractMethodBody(code, "private static void RaiseLiveRegionChanged(")
            .Should().MatchRegex(@"RaiseAutomationEvent\s*\(\s*AutomationEvents\.LiveRegionChanged\s*\)",
                "AutomationProperties.LiveSetting='Polite' 単独では発火しないため、" +
                "明示的な RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) 呼び出しが必須" +
                "（Issue #1509 で StaffAuthDialog に確立されたパターン）。");
    }

    /// <summary>
    /// Issue #1548: OperationLogDialog のコードビハインドが ViewModel.PropertyChanged を購読・解除していること。
    /// ViewModel バインド駆動の TextBlock に対する LiveRegion 通知は、ViewModel 側のプロパティ変化を起点とするため、
    /// 購読と解除の両方が必要（解除漏れはメモリリーク）。
    /// </summary>
    [Fact]
    public void OperationLogDialog_CodeBehindで_ViewModelPropertyChangedを購読と解除していること()
    {
        var code = ReadCodeOnly("OperationLogDialog.xaml.cs");

        Regex.IsMatch(code, @"PropertyChanged\s*\+=\s*OnViewModelPropertyChanged\b").Should().BeTrue(
            "ViewModel の PropertyChanged を購読していること。");
        Regex.IsMatch(code, @"PropertyChanged\s*-=\s*OnViewModelPropertyChanged\b").Should().BeTrue(
            "Window.Closed 等で PropertyChanged を解除していること（メモリリーク防止）。");
    }

    /// <summary>
    /// Issue #1548/#1507: CurrentPageNumberText TextBlock は単一 Text バインドであること。
    /// 以前は &lt;Run Text="{Binding CurrentPage}"/&gt; など 4 つの Run で構成されていたが、Run 構成では
    /// Inlines 変更が親 TextBlock の Text プロパティ更新を伴わず TextBlockAutomationPeer の Name キャッシュが
    /// invalidate されないため、コードビハインドの LiveRegionChanged 発火が Narrator に届かなかった。
    /// 派生プロパティ PageNumberDisplay 経由の Text 単一バインドに回帰しないよう静的解析で固定する。
    /// </summary>
    [Fact]
    public void OperationLogDialog_CurrentPageNumberTextは_単一Textバインドであること()
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadDialog("OperationLogDialog.xaml"));

        var elements = XamlElementInspection.EnumerateElements(xaml, "TextBlock")
            .Where(e => XamlElementInspection.GetAttribute(e.StartTag, "x:Name") == "CurrentPageNumberText")
            .ToList();
        elements.Should().ContainSingle("CurrentPageNumberText の TextBlock がちょうど 1 つ存在すること");
        var element = elements[0];

        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(element.StartTag, "Text"))
            .Should().Be("PageNumberDisplay",
            "Issue #1548/#1507: CurrentPageNumberText は <Run> 構成ではなく Text=\"{Binding PageNumberDisplay}\" の単一バインドにすること。" +
            "Run 構成では TextBlockAutomationPeer の Name キャッシュが invalidate されず LiveRegionChanged が Narrator に届かない。");

        XamlElementInspection.EnumerateElements(element.Body, "Run").Should().BeEmpty(
            "Issue #1548/#1507: CurrentPageNumberText の子要素として <Run> を使わないこと。");
    }

    // ------------------------------------------------------------------
    // 判定
    // ------------------------------------------------------------------

    /// <summary>コメントを除き、指定した属性を開始タグに持つ要素の数を返す。</summary>
    private static int CountStartTagsWithAttribute(string xaml, string attributeName)
        => XamlElementInspection.EnumerateStartTags(XamlElementInspection.StripXmlComments(xaml))
            .Count(t => XamlElementInspection.GetAttribute(t.StartTag, attributeName) != null);

    /// <summary>コメントを除き、<c>AutomationProperties.Name</c> が値まで一致する要素があるか。</summary>
    private static bool HasElementWithAutomationName(string xaml, string requiredName)
        => EnumerateStartTags(xaml)
            .Any(tag => XamlElementInspection.GetAttribute(tag, AutomationName) == requiredName);

    /// <summary>
    /// <c>Text="{Binding StatusMessage}"</c> の要素のうち、同じ開始タグに指定の LiveSetting を持つものがあるか。
    /// </summary>
    private static bool HasStatusMessageWithLiveSetting(string xaml, string liveSetting)
        => EnumerateStartTags(xaml)
            .Where(tag => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(tag, "Text")) == "StatusMessage")
            .Any(tag => XamlElementInspection.GetAttribute(tag, AutomationLiveSetting) == liveSetting);

    /// <summary>
    /// コメントを除き、指定した属性値を持つ開始タグがちょうど 1 つあることを表明して返す。
    /// </summary>
    private static string FindSingleStartTag(string xaml, string attributeName, string value)
    {
        var tags = EnumerateStartTags(xaml)
            .Where(tag => XamlElementInspection.GetAttribute(tag, attributeName) == value)
            .ToList();
        tags.Should().ContainSingle($"{attributeName}=\"{value}\" の要素がちょうど 1 つ存在すること（改名・削除で検査が空振りしないため）");
        return tags[0];
    }

    private static IEnumerable<string> EnumerateStartTags(string xaml)
        => XamlElementInspection.EnumerateStartTags(XamlElementInspection.StripXmlComments(xaml))
            .Select(t => t.StartTag);

    private static string ReadDialog(string fileName)
        => File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", fileName)));

    private static string ReadCodeBehind(string fileName) => ReadDialog(fileName);

    /// <summary>コメントと文字列リテラルを除いたコードビハインド。</summary>
    private static string ReadCodeOnly(string fileName)
        => TestSourceInspection.ToCodeOnly(ReadCodeBehind(fileName));
}
