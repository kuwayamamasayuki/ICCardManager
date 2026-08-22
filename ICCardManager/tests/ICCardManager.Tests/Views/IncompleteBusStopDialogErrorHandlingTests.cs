using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1817: <c>IncompleteBusStopDialog</c> の <c>Loaded</c> ハンドラーが、
/// 初期読み込みの失敗を「生の <c>ex.Message</c> のダイアログ」で見せていないことを、
/// ソーステキスト上の静的検査で固定する。
/// </summary>
/// <remarks>
/// <para>
/// この経路は <c>Window</c> のコードビハインド（コンストラクタ内の <c>Loaded</c> ラムダ）にあり、
/// WPF の <c>Window</c> は STA スレッドでしか生成できないため<b>xUnit から実行できない</b>。
/// そのため挙動テストではなくソーステキストの静的検査で守る
/// （<c>CardManageDialogStatusAreaLayoutTests</c> / <c>BusyScopeDialogConventionTests</c> と同じ方式）。
/// </para>
/// <para>
/// 修正前は <c>MessageBox.Show($"データの読み込み中にエラーが発生しました。\n\n{ex.Message}", …)</c> で、
/// 共有モードの DB ロック・UNC 断で発生する SQLite／.NET の英語文言がそのまま職員に提示され
/// （Issue #1614 違反）、かつ <c>ErrorDialogHelper.LogException</c> も呼んでいなかったため
/// <b>ログにも一切残らなかった</b>。「エラーが出た」という申告だけがあって調査の起点が無い状態になる。
/// </para>
/// <para>
/// 検査は「禁止された形の不在」だけでなく「正しい形の存在」も併せて表明する。
/// 前者だけだと、<c>catch</c> ブロックごと消して無言で握りつぶす実装でも緑になる。
/// </para>
/// </remarks>
public class IncompleteBusStopDialogErrorHandlingTests
{
    /// <summary>検査対象（本番ソース）。</summary>
    private static string SourcePath => Path.Combine(
        TestPaths.GetProductionSourceRoot(),
        "Views", "Dialogs", "IncompleteBusStopDialog.xaml.cs");

    /// <summary>
    /// <c>ex.Message</c> を文字列補間・連結で UI 文言へ埋め込む形。
    /// <c>ErrorDialogHelper.LogException(ex, …)</c> のようなログ経路は
    /// <c>ex.Message</c> を書かないため一致しない。
    /// </summary>
    private static readonly Regex RawExceptionMessageUsage =
        new Regex(@"(?<![\w.])ex\.Message", RegexOptions.Compiled);

    /// <summary>コメント・文字列リテラルを剥がした本番ソース。</summary>
    /// <remarks>
    /// 剥がさないと、この欠陥の由来を説明したコメント自身が違反として検出される
    /// （<c>.claude/rules/development-conventions.md</c> の「極性の反転」）。
    /// </remarks>
    private static string CodeOnlySource()
        => TestSourceInspection.ToCodeOnly(File.ReadAllText(SourcePath));

    /// <summary>
    /// <c>Loaded</c> ハンドラーを含むコンストラクター本体だけを取り出す。
    /// </summary>
    /// <remarks>
    /// ファイル全体を対象にすると、<c>OpenBusStopInputAsync</c> の再読み込み失敗用に
    /// 別途置かれている <c>ErrorDialogHelper.LogException</c>（Issue #1816）が
    /// 「存在」の検査を満たしてしまい、<b><c>Loaded</c> のログを消しても緑になる</b>。
    /// 検査対象は必ず <c>Loaded</c> ハンドラーを含むブロックへ絞ること。
    /// </remarks>
    private static string ConstructorBody()
        => TestSourceInspection.ExtractMethodBody(
            CodeOnlySource(), "public IncompleteBusStopDialog(");

    [Fact]
    [Trait("Category", "Unit")]
    public void Loaded失敗時に生の例外メッセージをUIへ出さないこと()
    {
        var code = CodeOnlySource();

        RawExceptionMessageUsage.IsMatch(code).Should().BeFalse(
            "生の ex.Message は英語・技術用語を含みうるため職員には解読できない。"
            + "UI へは ExceptionMessageFormatter.ToUserMessage の 3 要素文言だけを出すこと"
            + "（Issue #1614、#1817）");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Loaded失敗時にユーザー向け文言とログの両方を経由すること()
    {
        var constructorBody = ConstructorBody();

        constructorBody.Should().Contain("ExceptionMessageFormatter.ToUserMessage",
            "UI 文言は 3 要素へ変換してから表示する（Issue #1614）");
        constructorBody.Should().Contain("ErrorDialogHelper.LogException",
            "この経路には ILogger が無いため、技術的詳細はファイルログへ逃がす。"
            + "ログが無いと『エラーが出た』という申告だけが残り調査の起点が無くなる（Issue #1817）");
    }

    /// <summary>
    /// 検査対象が実在し、抽出が空振りしていないことを表明する。
    /// </summary>
    /// <remarks>
    /// ファイルの移動・改名や <see cref="TestSourceInspection.ToCodeOnly"/> の
    /// 挙動変化で本文が空になると、上の 2 件は<b>検査せずに緑</b>になる。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void 検査対象のソースが抽出できていること()
    {
        File.Exists(SourcePath).Should().BeTrue(
            $"検査対象が見つからない: {SourcePath}");

        var code = CodeOnlySource();

        code.Should().Contain("class IncompleteBusStopDialog",
            "コメント・文字列を剥がした後も本文が残っていること");
        code.Should().Contain("MessageBox.Show",
            "検査したい失敗通知そのものが残っていること");

        var constructorBody = ConstructorBody();

        constructorBody.Should().Contain("Loaded +=",
            "抽出したコンストラクター本体に Loaded ハンドラーが含まれていること"
            + "（含まれないと『両方を経由すること』の検査が別の場所を見る）");
        constructorBody.Should().Contain("MessageBox.Show",
            "検査対象の失敗通知がコンストラクター本体の中にあること");
    }
}
