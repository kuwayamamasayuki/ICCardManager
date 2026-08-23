using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// 「<c>MessageBox</c> はオーナーを渡して表示する」という規約を、
/// <b>本番ソース全体</b>のソーステキスト上で固定する（Issue #1837）。
/// </summary>
/// <remarks>
/// <para>
/// オーナーを渡さない <c>MessageBox.Show(message, title, button, image)</c> は、WPF が
/// <c>GetActiveWindow()</c> でオーナーを解決する。<b>呼び出しスレッドがフォアグラウンドでない
/// ときこれは NULL になり</b>、下位のウィンドウが無効化されない（ownerless）。処理中オーバーレイを
/// クリックシールドとして兼用しなくなった現在（#1383 / #1784 / #1793）、ダイアログの背後の
/// ボタンが押せてしまう。
/// </para>
/// <para>
/// Issue #1794 は <c>DialogService</c> の 5 メソッドだけを是正し、その静的検査も
/// <c>DialogService.cs</c> 内に閉じていた。本検査はそれをリポジトリ全体へ広げる。
/// <b>移行が完了してから入れている</b>のは、先に入れると大量の抑制リストが必要になり
/// 「ビルド警告ゼロ」と同じ形骸化を招くため（Issue #1786）。
/// </para>
/// <para>
/// 許される表示手段は 3 つだけ:
/// </para>
/// <list type="number">
/// <item><c>Window</c> のコードビハインド: <c>MessageBox.Show(this, …)</c></item>
/// <item>ViewModel: <c>IDialogService</c> 経由</item>
/// <item>上のどちらも使えない層: <c>Common.OwnedMessageBox</c></item>
/// </list>
/// <para>
/// <b>「禁止された形の不在」と「正しい形の存在」を対で表明する。</b>不在だけを見ると、
/// 走査対象が 0 件へ縮んだ状態でも緑になる（<c>.claude/rules/error-messages.md</c> #1764）。
/// ただし「各対象が非空であること」では表明しない — 規約が推奨する方向の変更
/// （コードビハインドの <c>IDialogService</c> 化）でテストが赤になり、修正者を
/// 「対象から外す」方向へ誘導するため（#1786）。代わりに<b>検査ロジック自体を既知の
/// サンプル入力で固定</b>する。
/// </para>
/// </remarks>
public class MessageBoxOwnerConventionTests
{
    /// <summary>
    /// ownerless フォールバックを持つことが許される唯一のファイル（本番ソースルートからの相対パス）
    /// </summary>
    private const string FallbackFile = @"Common\OwnedMessageBox.cs";

    /// <summary>
    /// <c>MessageBox.Show(</c> の呼び出し。<c>OwnedMessageBox.Show(</c> は別物なので除外する
    /// （<c>(?&lt;!Owned)</c> が無いと集約先への委譲まで違反として数えてしまう＝極性の反転）。
    /// </summary>
    private static readonly Regex DirectCallPattern = new Regex(@"(?<![\w.])MessageBox\.Show\s*\(");

    /// <summary>
    /// 第 1 引数に <c>this</c>（自ウィンドウ）を渡している呼び出し
    /// </summary>
    private static readonly Regex OwnedByThisPattern = new Regex(@"(?<![\w.])MessageBox\.Show\s*\(\s*this\s*,");

    private static IReadOnlyList<string> GetProductionSourceFiles()
        => Directory.GetFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// ビルド生成物（<c>obj/</c> 配下の <c>*.g.cs</c> 等）は規約の対象外
    /// </summary>
    private static bool IsGenerated(string path)
    {
        var relative = ToRelative(path);
        return relative.StartsWith(@"obj\", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(@"bin\", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRelative(string path)
        => path.Substring(TestPaths.GetProductionSourceRoot().Length).TrimStart(Path.DirectorySeparatorChar, '/')
            .Replace('/', '\\');

    /// <summary>
    /// 違反（オーナーを渡していない <c>MessageBox.Show</c>）を列挙する
    /// </summary>
    /// <remarks>
    /// コメントと文字列リテラルを剥がしてから検査する。剥がさないと、
    /// <b>規約の理由を書いたコメント自体</b>が違反として検出される（#1692 の極性の反転）。
    /// </remarks>
    internal static IReadOnlyList<string> FindOwnerlessCalls(string relativePath, string source)
    {
        var code = TestSourceInspection.ToCodeOnlyPreservingLines(source);
        var violations = new List<string>();

        foreach (Match match in DirectCallPattern.Matches(code))
        {
            var ownedByThis = OwnedByThisPattern.Match(code, match.Index);
            if (ownedByThis.Success && ownedByThis.Index == match.Index)
            {
                continue;
            }

            var line = code.Take(match.Index).Count(c => c == '\n') + 1;
            violations.Add($"{relativePath}:{line}");
        }

        return violations;
    }

    [Fact]
    public void 本番ソースにオーナー無しのMessageBox表示が無いこと()
    {
        var violations = new List<string>();

        foreach (var path in GetProductionSourceFiles())
        {
            var relative = ToRelative(path);
            if (string.Equals(relative, FallbackFile, StringComparison.OrdinalIgnoreCase))
            {
                // 唯一のフォールバック地点。ここだけは owner==null のとき ownerless で表示する
                continue;
            }

            violations.AddRange(FindOwnerlessCalls(relative, File.ReadAllText(path)));
        }

        violations.Should().BeEmpty(
            "MessageBox はオーナー付きで表示すること（Window は this、ViewModel は IDialogService、"
            + "そのどちらも使えない層は Common.OwnedMessageBox）。Issue #1837");
    }

    [Fact]
    public void ownerlessフォールバックが集約先の1か所だけに存在すること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), FallbackFile.Replace('\\', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"{FallbackFile} が存在すること（集約先が移動したら本検査も追随させる）");

        var code = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(path));

        // 抽出範囲の妥当性を先に固定する（#1794 の作法）。集約先の中身が変わって
        // 検査が空振りしたまま緑になることを防ぐ。
        code.Should().Contain("owner != null", "集約先がオーナー有無で分岐していること");

        DirectCallPattern.Matches(code).Count.Should().Be(2,
            "集約先はオーナー有りと ownerless の 2 分岐だけを持つこと");
        new Regex(@"(?<![\w.])MessageBox\.Show\s*\(\s*owner\s*,").Matches(code).Count.Should().Be(1,
            "解決済みオーナーを渡す分岐が 1 つあること");

        // 他のファイルは上のテストで「オーナー無しの呼び出しがゼロ」と表明済み。
        // したがってアプリ全体の ownerless フォールバックはここの 1 分岐だけになる。
        (DirectCallPattern.Matches(code).Count
            - new Regex(@"(?<![\w.])MessageBox\.Show\s*\(\s*owner\s*,").Matches(code).Count)
            .Should().Be(1, "ownerless フォールバックはアプリ全体で 1 か所であること（Issue #1837）");
    }

    /// <summary>
    /// 検査ロジックそのものを既知のサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（本番ソース）が「違反ゼロ」に保たれる以上、上の 2 件だけでは
    /// <b>検査が何も見ていない状態</b>と区別が付かない。#1786 の「空振り検出を
    /// 『各対象が非空であること』で書かない」に従い、判定側をサンプルで表明する。
    /// </remarks>
    [Theory]
    // 違反: オーナー無しのオーバーロード
    [InlineData("MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);", 1)]
    // 適合: 自ウィンドウをオーナーに渡す（改行を挟む実際の書き方も含む）
    [InlineData("MessageBox.Show(this, msg, title, MessageBoxButton.OK, MessageBoxImage.Error);", 0)]
    [InlineData("MessageBox.Show(\n    this,\n    msg, title, MessageBoxButton.OK, MessageBoxImage.Error);", 0)]
    // 適合: 集約先への委譲は別物（Owned が前置されている）
    [InlineData("OwnedMessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);", 0)]
    [InlineData("Common.OwnedMessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);", 0)]
    // 適合: 規約の理由を書いたコメント・文言中の記述は違反にしない（極性の反転を防ぐ）
    [InlineData("// MessageBox.Show(msg, title, b, i) は使わない", 0)]
    [InlineData("var s = \"MessageBox.Show(msg, title, b, i)\";", 0)]
    public void 検査ロジックが違反と適合を見分けること(string snippet, int expectedViolations)
    {
        FindOwnerlessCalls("Sample.cs", snippet).Should().HaveCount(expectedViolations);
    }
}
