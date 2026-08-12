using System.IO;
using System.Linq;
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
/// 検査は「守りたい性質」ではなく「その性質を破れる経路」から設計する（開発規約の
/// ガード設計の項）。IsCancel の禁止は「閉じる」ボタンだけでなく<b>XAML 全体</b>を見る
/// （他のボタンに付いても Escape は同じ経路を通るため）。OnClosing の検査は
/// <b>メソッド本体</b>を取り出して <c>CanClose</c> の呼び出しと <c>e.Cancel</c> の設定を
/// 併せて確かめる（存在確認だけでは、空の override と別メソッドの CanClose 呼び出しで素通りする）。
/// Escape の経路は XAML・ViewModel・View の 3 リンクからなるため、markup で保証されていた
/// IsCancel と違い、View 側の <c>OnCloseRequested</c> 代入も併せて固定する。
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
    /// XAML のどのボタンも IsCancel を宣言していないこと。
    /// </summary>
    /// <remarks>
    /// 「閉じる」ボタンだけを見ると、後から追加された別ボタンの IsCancel を見逃す。
    /// Escape はどのボタンに付いていても同じ経路（Click → 無条件の DialogResult=false）を通るため、
    /// 禁止はダイアログ全体に及ぶ。
    /// </remarks>
    [Fact]
    public void XAML全体でIsCancelを宣言しないこと()
    {
        var xaml = File.ReadAllText(XamlPath);

        // 「IsCancel は付けない」という規約コメント自体を違反と誤検出しないよう、属性の形で照合する
        Regex.IsMatch(xaml, @"IsCancel\s*=\s*""True""").Should().BeFalse(
            "IsCancel は Click 処理の後に無条件で DialogResult=false を設定するため、" +
            "OnClosing の破棄確認で「いいえ」を選んでも DialogResult が false のまま残り、" +
            "以後の Escape / クリックが無反応になる（Issue #1743）。" +
            "「閉じる」以外のボタンに付けても同じ経路を通るため、ダイアログ全体で禁止する");

        // 空振り防止: 「閉じる」ボタンが存在することを併せて確かめる
        // （ボタンごと消えても上の NotMatch は成立してしまう）
        ExtractCloseButton().Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Escape キーが RequestCloseCommand へ配線されていること。
    /// IsCancel を外しただけだと Escape で閉じられなくなり、
    /// キーボード操作の一貫性規約（Issue #1615）を破る。
    /// </summary>
    [Fact]
    public void EscapeキーはRequestCloseCommandへ配線されていること()
    {
        var keyBinding = ExtractEscapeKeyBinding();

        GetAttribute(keyBinding, "Command").Should().Be("{Binding RequestCloseCommand}",
            "Escape は KeyBinding → RequestCloseCommand → Window.Close() の経路で" +
            "OnClosing の破棄確認を通す（Issue #1743）");
    }

    /// <summary>
    /// コードビハインドが OnClosing で破棄確認を一元化していること。
    /// </summary>
    /// <remarks>
    /// 「OnClosing がある」「ファイルのどこかに CanClose がある」だけでは、
    /// 空の override を残したまま確認を Click ハンドラへ戻す改変を検出できない。
    /// メソッド本体を取り出して、判定の委譲とクローズの中止の両方を確かめる。
    /// </remarks>
    [Fact]
    public void OnClosingがCanCloseを呼びクローズを中止すること()
    {
        var body = ExtractMethodBody("protected override void OnClosing");

        body.Should().Contain(".CanClose(",
            "破棄確認の判定は ViewModel.CanClose へ委譲し、単体テスト可能な形へ一元化する（Issue #1743）");

        Regex.IsMatch(body, @"e\.Cancel\s*=\s*true").Should().BeTrue(
            "CanClose が false を返したときに e.Cancel = true でクローズを止めなければ、" +
            "確認で「いいえ」を選んでもダイアログは閉じる（Issue #1743）");
    }

    /// <summary>
    /// CloseButton_Click が確認ダイアログを直接出さないこと（確認は OnClosing に一元化）。
    /// </summary>
    [Fact]
    public void CloseButton_Clickは確認ダイアログを直接出さないこと()
    {
        var body = ExtractMethodBody("private void CloseButton_Click");

        body.Should().NotContain("MessageBox.Show",
            "確認を Click ハンドラに置くと ✕ / Alt+F4 経由で迂回され、OnClosing の確認と" +
            "二重に出る経路も生まれる。確認は OnClosing に一元化する（Issue #1743）");

        body.Should().Contain("Close()",
            "CloseButton_Click は Close() を呼ぶだけの形であるはず（確認は OnClosing が担う）");
    }

    /// <summary>
    /// Escape の経路の最終リンク（ViewModel のクローズ要求 → Window.Close()）が配線されていること。
    /// </summary>
    /// <remarks>
    /// IsCancel は markup だけで Esc→クローズを保証していたが、置き換え後の経路は
    /// XAML の KeyBinding・ViewModel の RequestClose・View の代入という 3 リンクに分かれる。
    /// 代入が落ちると RequestClose() は null デリゲートを呼んで無言で返り、Escape が効かなくなる。
    /// ViewModel テストは自前のフェイクを渡すためこのリンクを観測できない。
    /// </remarks>
    [Fact]
    public void コードビハインドがOnCloseRequestedをCloseへ配線していること()
    {
        var code = ToCodeOnly(File.ReadAllText(CodeBehindPath));

        Regex.IsMatch(code, @"OnCloseRequested\s*=\s*Close\s*;").Should().BeTrue(
            "Escape → RequestCloseCommand → OnCloseRequested → Window.Close() の最終リンク。" +
            "代入が落ちると Escape が無言で効かなくなり、マニュアル §5.4 のショートカット表と食い違う（Issue #1743）");
    }

    /// <summary>
    /// 抽出ロジックが属性順・インデントに依存しないこと（既知のサンプル入力で固定）。
    /// </summary>
    /// <remarks>
    /// 抽出が脆いと、規約を満たす書き換え（属性順の入れ替え、file-scoped namespace への移行等）で
    /// 偽の赤が出て、次の保守者をガードの弱体化・削除へ誘導する。実データが将来変わっても
    /// 検査ロジック自体の正しさを保てるよう、サンプル入力で固定する。
    /// </remarks>
    [Fact]
    public void 抽出ロジックが属性順とインデントに依存しないこと()
    {
        // KeyBinding: Command が Key より前にあっても、間に別属性が挟まっても取り出せる
        var reordered = ExtractEscapeKeyBindingFrom(
            @"<Window.InputBindings>
                <KeyBinding Command=""{Binding RequestCloseCommand}"" Modifiers=""None"" Key=""Escape""/>
              </Window.InputBindings>");
        GetAttribute(reordered, "Command").Should().Be("{Binding RequestCloseCommand}");

        // メソッド本体: 閉じ波括弧のインデントに依存しない（file-scoped namespace でも動く）
        var body = ExtractMethodBodyFrom(
            "class C { private void CloseButton_Click(object s, EventArgs e) { if (x) { Y(); } Close(); } }",
            "private void CloseButton_Click");
        body.Should().Contain("Close()");
        body.Should().Contain("Y()", "入れ子の波括弧でメソッド本体の抽出が途中で切れないこと");
    }

    /// <summary>
    /// 「閉じる」ボタンの Button 要素全文を抽出する（属性順に依存しない）。
    /// </summary>
    private static string ExtractCloseButton()
    {
        var xaml = File.ReadAllText(XamlPath);

        var element = Regex.Matches(xaml, @"<Button\b[^>]*?/>")
            .Cast<Match>()
            .Select(m => m.Value)
            .FirstOrDefault(e => GetAttribute(e, "Content") == "閉じる");

        element.Should().NotBeNull("LedgerDetailDialog.xaml に「閉じる」ボタンが存在するはず");

        // 抽出範囲がずれて別のボタンを検査していないことを確かめる
        GetAttribute(element!, "Click").Should().Be("CloseButton_Click",
            "この検査は CloseButton_Click を持つ「閉じる」ボタンが対象。" +
            "ハンドラ名が変わったら本テストの抽出も追随させる");

        return element!;
    }

    /// <summary>
    /// Escape の KeyBinding 要素全文を抽出する（属性順に依存しない）。
    /// </summary>
    private static string ExtractEscapeKeyBinding() =>
        ExtractEscapeKeyBindingFrom(File.ReadAllText(XamlPath));

    private static string ExtractEscapeKeyBindingFrom(string xaml)
    {
        var element = Regex.Matches(xaml, @"<KeyBinding\b[^>]*?/>")
            .Cast<Match>()
            .Select(m => m.Value)
            .FirstOrDefault(e => GetAttribute(e, "Key") == "Escape");

        element.Should().NotBeNull(
            "Escape の KeyBinding が存在すること（IsCancel を外した以上、Esc の手段はここだけ）");

        return element!;
    }

    /// <summary>
    /// コードビハインドから指定シグネチャのメソッド本体を取り出す。
    /// </summary>
    private static string ExtractMethodBody(string signatureMarker) =>
        ExtractMethodBodyFrom(File.ReadAllText(CodeBehindPath), signatureMarker);

    private static string ExtractMethodBodyFrom(string source, string signatureMarker) =>
        TestSourceInspection.ExtractMethodBody(ToCodeOnly(source), signatureMarker);

    private static string ToCodeOnly(string source) => TestSourceInspection.ToCodeOnly(source);

    /// <summary>
    /// 要素テキストから属性値を取り出す（見つからなければ null）。
    /// </summary>
    private static string? GetAttribute(string element, string attributeName)
    {
        var match = Regex.Match(element, attributeName + @"\s*=\s*""(?<value>[^""]*)""");
        return match.Success ? match.Groups["value"].Value : null;
    }
}
