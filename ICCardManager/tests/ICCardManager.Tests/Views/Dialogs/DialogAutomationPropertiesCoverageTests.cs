using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1468: スクリーンリーダー対応の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 対象ダイアログでアクセシビリティ関連属性（<c>AutomationProperties.Name</c> /
/// <c>AutomationProperties.HelpText</c> / <c>AutomationProperties.LiveSetting</c>）の
/// 付与数が、Issue #1468 修正時点の水準を下回らないことを静的解析で検証する。
/// </para>
/// <para>
/// 動的に <c>Text</c> が書き換わる <c>TextBlock</c> では <c>AutomationProperties.Name</c> を
/// 設定すると Text のフォールバック読み上げが上書きされてしまうため、
/// <c>HelpText</c> + <c>LiveSetting</c> の組み合わせを採用している。
/// </para>
/// <para>
/// 実際にスクリーンリーダー（NVDA / Narrator）で読み上げられるかは UI 自動化を要し、
/// PR テストプランで手動検証する。本テストは XAML 上の付与漏れを早期検出するための
/// 静的セーフティネット。
/// </para>
/// </remarks>
public class DialogAutomationPropertiesCoverageTests
{
    private static readonly string DialogsDirectory = ResolveDialogsDirectory();

    /// <summary>
    /// 各ダイアログにおける <c>AutomationProperties.Name</c> の最低出現回数。
    /// Issue #1468 修正時点の付与数を回帰防止用の閾値として固定する。
    /// 値を増やす変更は許可（カバレッジ向上）、減らす変更は要レビュー（カバレッジ後退）。
    /// </summary>
    public static TheoryData<string, int> MinimumNameCounts => new()
    {
        // OperationLogDialog: 期間ボタン3個 + 検索条件4個 + 検索/クリア2個 +
        // ページネーション4個 + ページサイズ + DataGrid + Window + エクスポート2個 + 閉じる + 処理中 = 19個以上
        { "OperationLogDialog.xaml", 18 },
        // StaffAuthDialog: Window + アイコン + キャンセル + 仮想タッチ = 4個以上
        { "StaffAuthDialog.xaml", 4 },
    };

    /// <summary>
    /// 各ダイアログにおける <c>AutomationProperties.HelpText</c> の最低出現回数。
    /// </summary>
    public static TheoryData<string, int> MinimumHelpTextCounts => new()
    {
        { "OperationLogDialog.xaml", 18 },
        { "StaffAuthDialog.xaml", 6 },
    };

    [Theory]
    [MemberData(nameof(MinimumNameCounts))]
    public void Dialog_should_meet_minimum_AutomationProperties_Name_coverage(string xamlFileName, int minimumCount)
    {
        var xaml = ReadDialog(xamlFileName);
        var actualCount = Regex.Matches(xaml, @"AutomationProperties\.Name\b").Count;

        actualCount.Should().BeGreaterThanOrEqualTo(minimumCount,
            $"{xamlFileName}: AutomationProperties.Name の付与数が {actualCount} で、" +
            $"Issue #1468 修正時点の最低水準 {minimumCount} を下回っている。" +
            "スクリーンリーダー利用ユーザーがコントロールを識別できなくなるおそれがあるため、" +
            "削除・統合した場合は MinimumNameCounts の値を見直すか、別のコントロールに付与し直すこと。");
    }

    [Theory]
    [MemberData(nameof(MinimumHelpTextCounts))]
    public void Dialog_should_meet_minimum_AutomationProperties_HelpText_coverage(string xamlFileName, int minimumCount)
    {
        var xaml = ReadDialog(xamlFileName);
        var actualCount = Regex.Matches(xaml, @"AutomationProperties\.HelpText\b").Count;

        actualCount.Should().BeGreaterThanOrEqualTo(minimumCount,
            $"{xamlFileName}: AutomationProperties.HelpText の付与数が {actualCount} で、" +
            $"Issue #1468 修正時点の最低水準 {minimumCount} を下回っている。" +
            "操作のヒント情報を補強できなくなるため、HelpText を削減する場合は閾値を見直すこと。");
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

        xaml.Should().MatchRegex(BuildAutomationNamePattern(requiredName),
            $"OperationLogDialog: 主要コントロールに AutomationProperties.Name=\"{requiredName}\" が必要。" +
            "Issue #1468 で業務監査画面（操作ログ）のスクリーンリーダー対応を改善した際の付与項目。" +
            "（Issue #1504: XAML 整形で `=` 前後にスペースが入っても検出できるよう Regex マッチを採用）");
    }

    /// <summary>
    /// Issue #1504: <see cref="BuildAutomationNamePattern"/> が空白挿入を許容しつつ
    /// 値の厳密一致を維持していることを、合成 XAML サンプルで固定する回帰テスト。
    /// </summary>
    [Theory]
    [InlineData("<Button AutomationProperties.Name=\"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button AutomationProperties.Name = \"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button AutomationProperties.Name  =  \"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button\n    AutomationProperties.Name=\"検索を実行\" />", "検索を実行", true)]
    [InlineData("<Button AutomationProperties.Name=\"別の語\" />", "検索を実行", false)]
    [InlineData("<Button AutomationProperties.Name=\"検索\" />", "検索を実行", false)]
    [InlineData("", "検索を実行", false)]
    public void AutomationNamePattern_should_be_whitespace_tolerant_but_value_strict(
        string syntheticXaml, string requiredName, bool expectedMatch)
    {
        var pattern = BuildAutomationNamePattern(requiredName);

        Regex.IsMatch(syntheticXaml, pattern).Should().Be(expectedMatch,
            $"requiredName='{requiredName}' に対するパターンは、空白の有無や改行を許容しつつ" +
            "値の部分一致や別文字列を取り違えてはならない（Issue #1504）。" +
            $"入力: {syntheticXaml.Replace("\n", "\\n")}");
    }

    /// <summary>
    /// XAML 上の <c>AutomationProperties.Name="…"</c> 表記を、空白・改行を許容しつつ
    /// 値部分のみ厳密一致でマッチする正規表現を組み立てる（Issue #1504）。
    /// </summary>
    /// <remarks>
    /// Visual Studio の XAML 整形で属性が <c>Name = "…"</c> のように展開される場合や、
    /// 属性ごとに改行される場合でもマッチさせるため、<c>=</c> の前後と前置部に <c>\s*</c> を許容する。
    /// 値（<paramref name="requiredName"/>）は <see cref="Regex.Escape(string)"/> でエスケープし、
    /// メタ文字を含む語が来ても誤マッチしないようにする。
    /// </remarks>
    private static string BuildAutomationNamePattern(string requiredName)
        => $@"AutomationProperties\.Name\s*=\s*""{Regex.Escape(requiredName)}""";

    /// <summary>
    /// StaffAuthDialog（職員証認証）は最重要のダイアログであり、ステータス変化と
    /// タイムアウト残時間がスクリーンリーダーに通知される必要がある。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_status_text_should_have_assertive_live_setting()
    {
        var xaml = ReadDialog("StaffAuthDialog.xaml");

        Regex.IsMatch(xaml,
            @"x:Name=""StatusText""[^>]*?AutomationProperties\.LiveSetting=""Assertive""")
            .Should().BeTrue(
            "StaffAuthDialog: 認証ステータスは Assertive な LiveSetting で即時通知すべき。" +
            "認証成功・失敗の結果がスクリーンリーダー利用者に伝わらないと、誤操作の温床になる。" +
            "（Issue #1503: 要素境界を跨ぐ誤マッチ防止のため `[^>]*?` で同一開始タグ内に限定）");
    }

    [Fact]
    public void StaffAuthDialog_timeout_text_should_have_polite_live_setting()
    {
        var xaml = ReadDialog("StaffAuthDialog.xaml");

        Regex.IsMatch(xaml,
            @"x:Name=""TimeoutText""[^>]*?AutomationProperties\.LiveSetting=""Polite""")
            .Should().BeTrue(
            "StaffAuthDialog: タイムアウト残り時間は Polite で通知し、" +
            "頻繁な秒数更新がスクリーンリーダーの読み上げを阻害しないようにする。" +
            "（Issue #1503: 要素境界を跨ぐ誤マッチ防止のため `[^>]*?` で同一開始タグ内に限定）");
    }

    /// <summary>
    /// OperationLogDialog の検索ステータスメッセージは LiveSetting で変化を通知する。
    /// </summary>
    [Fact]
    public void OperationLogDialog_status_message_should_announce_changes()
    {
        var xaml = ReadDialog("OperationLogDialog.xaml");

        xaml.Should().MatchRegex(
            @"Text=""\{Binding StatusMessage\}""[^>]*?AutomationProperties\.LiveSetting=""Polite""",
            "OperationLogDialog: ステータスメッセージは Polite な LiveSetting で検索結果や件数の変化を通知すべき。" +
            "（Issue #1503: 要素境界を跨ぐ誤マッチ防止のため `[^>]*?` で同一開始タグ内に限定）");
    }

    /// <summary>
    /// Issue #1503: LiveSetting 検査 regex は同一要素内（同一開始タグ内）に閉じ込められるべき。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧来の <c>[\s\S]*?</c> は要素を跨いだ誤マッチを許してしまうため、
    /// <c>[^>]*?</c> で開始タグ内に限定する。XAML の属性値は <c>"..."</c> で囲まれており、
    /// 開始タグ内に裸の <c>&gt;</c> は出現しないため、<c>[^&gt;]</c> は同一開始タグ内に安全に留まる。
    /// 改行・空白も <c>[^&gt;]</c> でマッチするため、マルチライン属性レイアウトでも問題ない。
    /// </para>
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
        // production テスト (StatusText/TimeoutText/StatusMessage) と同等の意味論を持つ
        // 「同一開始タグ内マッチ」を [^>]*? で実現することを固定する（Issue #1503）。
        var pattern = @"Text=""\{Binding StatusMessage\}""[^>]*?AutomationProperties\.LiveSetting=""Polite""";

        Regex.IsMatch(syntheticXaml, pattern).Should().Be(expectedMatch,
            "Issue #1503: LiveSetting 検査 regex は同一開始タグ内に閉じ込めるべき。" +
            $"入力: {syntheticXaml.Replace("\n", "\\n")}");
    }

    /// <summary>
    /// Issue #1503: 旧 <c>[\s\S]*?</c> パターンが要素境界を跨ぐ誤マッチを起こしていた事実を
    /// 記録する回帰テスト。「なぜ <c>[^&gt;]*?</c> を採用したか」のドキュメントを兼ねる。
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
        var newSafePattern = @"Text=""\{Binding StatusMessage\}""[^>]*?AutomationProperties\.LiveSetting=""Polite""";

        Regex.IsMatch(crossElementXaml, oldUnsafePattern).Should().BeTrue(
            "Issue #1503 (回帰記録): 旧 `[\\s\\S]*?` regex は要素境界を考慮せず、" +
            "別 TextBlock の LiveSetting=\"Polite\" を誤検出していた。" +
            "本アサートは旧挙動を固定する記録目的。");

        Regex.IsMatch(crossElementXaml, newSafePattern).Should().BeFalse(
            "Issue #1503: 新 `[^>]*?` regex は同一開始タグ内に限定されるため、" +
            "別 TextBlock の LiveSetting=\"Polite\" を誤検出しない。");
    }

    /// <summary>
    /// 動的更新される TextBlock では AutomationProperties.Name を付けないこと。
    /// Name を付けると Text のフォールバック読み上げが上書きされ、内容変化が伝わらなくなる。
    /// </summary>
    [Theory]
    [InlineData("StaffAuthDialog.xaml", "OperationDescriptionText")]
    [InlineData("StaffAuthDialog.xaml", "StatusText")]
    [InlineData("StaffAuthDialog.xaml", "TimeoutText")]
    public void Dynamic_text_blocks_should_not_have_AutomationProperties_Name(string xamlFileName, string elementName)
    {
        var xaml = ReadDialog(xamlFileName);

        var match = Regex.Match(xaml,
            @"x:Name=""" + Regex.Escape(elementName) + @"""[^/]*?AutomationProperties\.Name=",
            RegexOptions.Singleline);

        match.Success.Should().BeFalse(
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
        var xaml = ReadDialog("StaffAuthDialog.xaml");

        Regex.IsMatch(xaml,
            @"x:Name=""StatusBorder""[^>]*?Visibility=""Collapsed""")
            .Should().BeFalse(
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
        var codeBehind = ReadCodeBehind("StaffAuthDialog.xaml.cs");

        codeBehind.Should().Contain(
            "RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)",
            "StaffAuthDialog: Text 更新だけでは LiveRegionChanged が確実に発火しないため、" +
            "ShowStatus 内で UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) を明示呼び出しすること（Issue #1509）。");
    }

    /// <summary>
    /// Issue #1509: 認証成功時にも ShowStatus でステータス表示し、
    /// スクリーンリーダーに認証成功を通知する。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_authentication_success_path_should_call_ShowStatus()
    {
        var codeBehind = ReadCodeBehind("StaffAuthDialog.xaml.cs");

        // OnCardRead の if (staff != null) { ... } ブロック内に ShowStatus 呼び出しが必要。
        // 「認証に成功」というメッセージリテラルで成功パスを識別する。
        Regex.IsMatch(codeBehind, @"ShowStatus\(\$?""認証に成功")
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
        var codeBehind = ReadCodeBehind("StaffAuthDialog.xaml.cs");

        // ShowStatus メソッド本体を抽出
        var showStatusBody = ExtractMethodBody(codeBehind, "ShowStatus");

        // 妥当性チェック: ExtractMethodBody が想定通りの範囲を抽出できているか確認する。
        // ShowStatus は必ず StatusText.Text に書き込むため、抽出本文に "StatusText" が含まれるはず。
        // 含まれない場合は ExtractMethodBody の制約（文字列/コメント中の {} 非対応）に
        // 引っかかっている可能性がある。
        showStatusBody.Should().Contain("StatusText",
            "ExtractMethodBody が ShowStatus の本体を正しく抽出できていない可能性。" +
            "ヘルパの制約（文字列リテラル内の {} 非対応など）に該当する変更が " +
            "ShowStatus に入った可能性があるため、ヘルパまたは ShowStatus の修正を検討すること。");

        Regex.IsMatch(showStatusBody, @"0x[0-9A-Fa-f]{2}")
            .Should().BeFalse(
            "StaffAuthDialog.ShowStatus: 色値リテラル（0xFF, 0xEB 等）は AccessibilityStyles.xaml の " +
            "ブラシキー（ErrorBackgroundBrush / SuccessBackgroundBrush 等）を FindResource 経由で参照すること（Issue #1392）。" +
            "現在のメソッド本体: " + showStatusBody);
    }

    /// <summary>
    /// code-behind ファイルを読み込む。Views/Dialogs/ 配下を想定。
    /// </summary>
    private static string ReadCodeBehind(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"code-behind {fileName} が存在すべき");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// 指定メソッドの本体（{ ... } の中身）をテキストから抽出する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 単純な中括弧バランススキャナ。本実装は C# の以下の構文を**認識しない**:
    /// 文字列リテラル内の <c>{</c>/<c>}</c>、補間文字列 <c>$"{expr}"</c>、
    /// 逐語的文字列 <c>@"..."</c>、char リテラル <c>'{'</c>、コメント。
    /// </para>
    /// <para>
    /// 静的検査ヘルパとしての利用想定: 対象メソッド本体に <c>{</c>/<c>}</c> を含む
    /// 文字列リテラル等が無いことが前提。新規に対象メソッドを追加する際は、
    /// 抽出結果に期待されるキーワードが含まれることをアサート側で確認すること。
    /// </para>
    /// </remarks>
    private static string ExtractMethodBody(string code, string methodName)
    {
        var pattern = $@"\b{Regex.Escape(methodName)}\s*\([^)]*\)\s*(?::\s*base\([^)]*\))?\s*\{{";
        var startMatch = Regex.Match(code, pattern);
        if (!startMatch.Success)
        {
            throw new InvalidOperationException($"メソッド {methodName} が見つかりません");
        }

        var start = startMatch.Index + startMatch.Length;
        var depth = 1;
        var pos = start;
        while (pos < code.Length && depth > 0)
        {
            if (code[pos] == '{') depth++;
            else if (code[pos] == '}') depth--;
            pos++;
        }
        return code.Substring(start, pos - start - 1);
    }

    private static string ReadDialog(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"ダイアログ {fileName} が存在すべき");
        return File.ReadAllText(path);
    }

    private static string ResolveDialogsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "src", "ICCardManager", "Views", "Dialogs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Views/Dialogs ディレクトリを {AppContext.BaseDirectory} の親階層から解決できませんでした");
    }
}
