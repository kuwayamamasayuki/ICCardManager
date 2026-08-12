using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1743: 利用履歴詳細ダイアログの未保存変更ガードが、タイトルバーの ✕ / Alt+F4 /
/// Escape / 「閉じる」ボタンのどの経路でも迂回されないことを固定する回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 破棄確認を「閉じる」ボタンの Click ハンドラに置く形は 2 つの理由で成立しない。
/// ①タイトルバーの ✕ / Alt+F4 は Click を通らず確認なしで閉じる。
/// ②<c>Button.IsCancel="True"</c> は Click 処理の<b>後</b>に無条件で DialogResult=false を
/// 設定するため、ハンドラ内の early-return では閉じるのを止められない。さらに Closing を
/// キャンセルしても DialogResult が false のまま残り、<c>DialogResult</c> セッターの
/// 値変化チェックにより以後の Escape / クリックが無反応になる。
/// したがって確認は OnClosing（すべてのクローズ経路が通る唯一の関門）へ一元化し、
/// IsCancel の代わりに Escape を KeyBinding → RequestCloseCommand → Window.Close() で配線する。
/// </para>
/// <para>
/// 実際のクローズ挙動の検証は WPF Window のインスタンス化と STA スレッドを要するため、
/// PR のテストプランで手動検証する。破棄確認の判定ロジック自体は
/// <c>LedgerDetailViewModelTests</c> の CanClose / RequestCloseCommand 系テストが担保し、
/// 本クラスは XAML とコードビハインドの配線を静的に固定する
/// （<c>DialogMinimumSizeTests</c> で確立した静的解析方式を踏襲）。
/// </para>
/// </remarks>
public class LedgerDetailDialogCloseGuardTests
{
    private static readonly string XamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "LedgerDetailDialog.xaml"));

    private static readonly string CodeBehindPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "LedgerDetailDialog.xaml.cs"));

    /// <summary>
    /// 「閉じる」ボタンが IsCancel を宣言していないこと。
    /// </summary>
    [Fact]
    public void 閉じるボタンはIsCancelを宣言しないこと()
    {
        var closeButton = ExtractCloseButton();

        closeButton.Should().NotMatchRegex(@"IsCancel\s*=\s*""True""",
            "IsCancel は Click 処理の後に無条件で DialogResult=false を設定するため、" +
            "OnClosing の破棄確認で「いいえ」を選んでも DialogResult が false のまま残り、" +
            "以後の Escape / クリックが無反応になる（Issue #1743）");
    }

    /// <summary>
    /// Escape キーが RequestCloseCommand へ配線されていること。
    /// IsCancel を外しただけだと Escape で閉じられなくなり、
    /// キーボード操作の一貫性規約（Issue #1615）を破る。
    /// </summary>
    [Fact]
    public void EscapeキーはRequestCloseCommandへ配線されていること()
    {
        var xaml = File.ReadAllText(XamlPath);

        Regex.IsMatch(xaml,
                @"<KeyBinding\s+Key\s*=\s*""Escape""\s+Command\s*=\s*""\{Binding\s+RequestCloseCommand\}""")
            .Should().BeTrue(
                "Escape は KeyBinding → RequestCloseCommand → Window.Close() の経路で" +
                "OnClosing の破棄確認を通す（Issue #1743）");
    }

    /// <summary>
    /// コードビハインドが OnClosing で破棄確認を一元化していること。
    /// </summary>
    [Fact]
    public void コードビハインドはOnClosingで破棄確認を一元化していること()
    {
        var code = File.ReadAllText(CodeBehindPath);

        Regex.IsMatch(code, @"protected\s+override\s+void\s+OnClosing\b").Should().BeTrue(
            "タイトルバーの ✕ / Alt+F4 もガードするには、すべてのクローズ経路が通る " +
            "OnClosing で判定する必要がある（Issue #1743）");

        code.Should().Contain(".CanClose(",
            "破棄確認の判定は ViewModel.CanClose へ委譲し、単体テスト可能な形へ一元化する（Issue #1743）");
    }

    /// <summary>
    /// CloseButton_Click が確認ダイアログを直接出さないこと（確認は OnClosing に一元化）。
    /// </summary>
    [Fact]
    public void CloseButton_Clickは確認ダイアログを直接出さないこと()
    {
        var method = ExtractCloseButtonClickMethod();

        method.Should().NotContain("MessageBox.Show",
            "確認を Click ハンドラに置くと ✕ / Alt+F4 経由で迂回され、OnClosing の確認と" +
            "二重に出る経路も生まれる。確認は OnClosing に一元化する（Issue #1743）");
    }

    /// <summary>
    /// 「閉じる」ボタンの Button 要素全文を抽出する。
    /// </summary>
    private static string ExtractCloseButton()
    {
        var xaml = File.ReadAllText(XamlPath);

        var match = Regex.Match(xaml, @"<Button\s+Content=""閉じる""[\s\S]*?/>");
        match.Success.Should().BeTrue("LedgerDetailDialog.xaml に「閉じる」ボタンが存在するはず");

        // 抽出範囲がずれて別のボタンを検査していないことを確かめる
        match.Value.Should().Contain(@"Click=""CloseButton_Click""",
            "この検査は CloseButton_Click を持つ「閉じる」ボタンが対象。" +
            "ハンドラ名が変わったら本テストの抽出も追随させる");

        return match.Value;
    }

    /// <summary>
    /// CloseButton_Click メソッドの本文を抽出する。
    /// </summary>
    private static string ExtractCloseButtonClickMethod()
    {
        var code = File.ReadAllText(CodeBehindPath);

        var match = Regex.Match(code,
            @"private\s+void\s+CloseButton_Click[\s\S]*?\n        \}");
        match.Success.Should().BeTrue("LedgerDetailDialog.xaml.cs に CloseButton_Click が存在するはず");

        // 抽出がメソッド末尾まで届いていることを確かめる（範囲が縮むと NotContain が空振りする）
        match.Value.Should().Contain("Close()",
            "CloseButton_Click は Close() を呼ぶだけの形であるはず（確認は OnClosing が担う）");

        return match.Value;
    }
}
