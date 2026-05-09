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

        xaml.Should().Contain($"AutomationProperties.Name=\"{requiredName}\"",
            $"OperationLogDialog: 主要コントロールに AutomationProperties.Name=\"{requiredName}\" が必要。" +
            "Issue #1468 で業務監査画面（操作ログ）のスクリーンリーダー対応を改善した際の付与項目。");
    }

    /// <summary>
    /// StaffAuthDialog（職員証認証）は最重要のダイアログであり、ステータス変化と
    /// タイムアウト残時間がスクリーンリーダーに通知される必要がある。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_status_text_should_have_assertive_live_setting()
    {
        var xaml = ReadDialog("StaffAuthDialog.xaml");

        Regex.IsMatch(xaml,
            @"x:Name=""StatusText""[\s\S]*?AutomationProperties\.LiveSetting=""Assertive""")
            .Should().BeTrue(
            "StaffAuthDialog: 認証ステータスは Assertive な LiveSetting で即時通知すべき。" +
            "認証成功・失敗の結果がスクリーンリーダー利用者に伝わらないと、誤操作の温床になる。");
    }

    [Fact]
    public void StaffAuthDialog_timeout_text_should_have_polite_live_setting()
    {
        var xaml = ReadDialog("StaffAuthDialog.xaml");

        Regex.IsMatch(xaml,
            @"x:Name=""TimeoutText""[\s\S]*?AutomationProperties\.LiveSetting=""Polite""")
            .Should().BeTrue(
            "StaffAuthDialog: タイムアウト残り時間は Polite で通知し、" +
            "頻繁な秒数更新がスクリーンリーダーの読み上げを阻害しないようにする。");
    }

    /// <summary>
    /// OperationLogDialog の検索ステータスメッセージは LiveSetting で変化を通知する。
    /// </summary>
    [Fact]
    public void OperationLogDialog_status_message_should_announce_changes()
    {
        var xaml = ReadDialog("OperationLogDialog.xaml");

        xaml.Should().MatchRegex(
            @"Text=""\{Binding StatusMessage\}""[\s\S]*?AutomationProperties\.LiveSetting=""Polite""",
            "OperationLogDialog: ステータスメッセージは Polite な LiveSetting で検索結果や件数の変化を通知すべき。");
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
