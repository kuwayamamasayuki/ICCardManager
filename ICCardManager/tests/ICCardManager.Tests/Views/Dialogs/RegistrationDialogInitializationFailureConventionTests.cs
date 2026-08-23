using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// 未登録カード経由の登録ダイアログが、初期化（<c>Loaded</c>）に失敗したときに
/// 必ず自分を閉じることをソーステキスト上で固定する規約テスト（Issue #1844）。
/// </summary>
/// <remarks>
/// <para>
/// これらのダイアログは <c>StartNew*WithIdmAsync</c> の入口でカード読み取り抑制を取得する
/// （Issue #1807）。加えてメイン画面側も、未登録カード経由の <c>ShowDialog</c> を
/// <c>using</c> スコープで囲んで抑制を保持している。どちらも解放されるのは
/// 「ダイアログが閉じたとき」（<c>Closed</c> → <c>Cleanup</c> ／ <c>ShowDialog</c> の復帰）だけである。
/// </para>
/// <para>
/// したがって初期化に失敗したまま開いたままにすると、編集フォームも出ていない画面が残り、
/// その間メイン画面は全カードタッチを無言で無視する（音も出ない）。
/// 復帰手段が「職員が自分でダイアログを閉じる」1 つしかない状態は
/// .claude/rules/development-conventions.md Issue #1725 が禁じた形そのものである。
/// </para>
/// <para>
/// <c>Close()</c> は <c>catch</c> の中の <c>finally</c> に置く。案内の表示（<c>OwnedMessageBox</c>）
/// 自体が失敗し得るため、後始末をその後ろに置くと二次例外で飛ばされる
/// （同 Issue #1745「catch の中の後始末は、それ自体が失敗し得ることを前提に書く」）。
/// </para>
/// <para>
/// 走査対象はファイル名で列挙せず、「<c>StartNew*WithIdmAsync</c> を呼ぶコードビハインド」という
/// コードの形から導出する。同型のダイアログが追加されたときに検査から静かに漏れないようにするため
/// （同 Issue #1786「ガードを書くときは『その性質を破れる全経路』を列挙する」）。
/// </para>
/// </remarks>
public class RegistrationDialogInitializationFailureConventionTests
{
    /// <summary>
    /// 未登録カード経由の登録開始（＝入口で抑制を取得する経路）を呼んでいるかどうかの判定。
    /// </summary>
    private static readonly Regex StartNewWithIdmPattern = new(
        @"StartNew\w*WithIdmAsync\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex LoadedHandlerPattern = new(
        @"private\s+async\s+void\s+\w*_Loaded\s*\(",
        RegexOptions.Compiled);

    private static string ViewsDirectory =>
        Path.Combine(TestPaths.GetSolutionRoot(), "src", "ICCardManager", "Views");

    private static IReadOnlyList<(string FileName, string CodeOnly)> LoadRegistrationDialogSources()
    {
        var files = Directory.GetFiles(ViewsDirectory, "*.xaml.cs", SearchOption.AllDirectories);

        files.Should().NotBeEmpty($"Views ディレクトリ（{ViewsDirectory}）が走査できること");

        return files
            .Select(f => (Path.GetFileName(f), TestSourceInspection.ToCodeOnly(File.ReadAllText(f))))
            .Where(x => StartNewWithIdmPattern.IsMatch(x.Item2))
            .ToList();
    }

    /// <summary>
    /// 未登録カード経由の登録ダイアログが実際に検出できること（空振り防止）。
    /// </summary>
    [Fact]
    public void 未登録カード経由の登録ダイアログを走査対象として検出できること()
    {
        var targets = LoadRegistrationDialogSources();

        targets.Should().HaveCountGreaterThanOrEqualTo(2,
            "カード管理ダイアログと職員管理ダイアログの 2 つが未登録カード経由の登録開始を呼ぶ");
        targets.Select(t => t.FileName).Should().Contain("CardManageDialog.xaml.cs");
        targets.Select(t => t.FileName).Should().Contain("StaffManageDialog.xaml.cs");
    }

    /// <summary>
    /// 初期化に失敗したら、案内の表示が失敗しても必ずダイアログを閉じること（Issue #1844）。
    /// </summary>
    [Fact]
    public void 初期化失敗時にダイアログを閉じて抑制を回収すること()
    {
        var violations = new List<string>();

        foreach (var (fileName, codeOnly) in LoadRegistrationDialogSources())
        {
            var loadedBody = ExtractLoadedHandlerBody(codeOnly);

            // 抽出が空振りしていないことを先に表明する
            // （.claude/rules/development-conventions.md Issue #1794）。
            loadedBody.Should().NotBeNullOrWhiteSpace($"{fileName} の Loaded ハンドラー本体を抽出できること");
            loadedBody.Should().Contain("InitializeAsync",
                $"{fileName} から抽出したのが初期化を行う Loaded ハンドラーであること");
            loadedBody.Should().MatchRegex(StartNewWithIdmPattern.ToString(),
                $"{fileName} から抽出した本体が未登録カード経由の登録開始を含むこと");

            if (!HasGuaranteedCloseOnFailure(loadedBody))
            {
                violations.Add(fileName);
            }
        }

        violations.Should().BeEmpty(
            "初期化に失敗した登録ダイアログは、catch の中の finally で Close() を呼び、" +
            "カード読み取り抑制（#1807）とメイン画面側の using スコープを回収すること（Issue #1844）。" +
            $"違反: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// 初期化失敗の案内に生の <c>ex.Message</c> を出さないこと（Issue #1614 / #1817）。
    /// </summary>
    [Fact]
    public void 初期化失敗の案内で生の例外メッセージを出さないこと()
    {
        foreach (var (fileName, codeOnly) in LoadRegistrationDialogSources())
        {
            var loadedBody = ExtractLoadedHandlerBody(codeOnly);

            loadedBody.Should().NotBeNullOrWhiteSpace($"{fileName} の Loaded ハンドラー本体を抽出できること");
            loadedBody.Should().NotMatchRegex(@"ex\s*\.\s*Message",
                $"{fileName}: 生の例外メッセージ（英語の SQLite エラー等）を職員へ出さない");
            loadedBody.Should().Contain("ToUserMessage",
                $"{fileName}: 3 要素の文言へ変換して案内すること");
            loadedBody.Should().Contain("LogException",
                $"{fileName}: 技術的詳細はログへ残すこと（ILogger を持たない層の正規手段）");
        }
    }

    #region 検査ロジックのサンプル入力による固定（実データが変わっても空振りしない）

    [Fact]
    public void 検査ロジック_catchでCloseを呼ばない形を違反として検出すること()
    {
        const string body = @"
            try { await _viewModel.InitializeAsync(); }
            catch (Exception ex) { ErrorDialogHelper.ShowError(ex, ""初期化エラー""); }
        ";

        HasGuaranteedCloseOnFailure(body).Should().BeFalse();
    }

    [Fact]
    public void 検査ロジック_catchの末尾でCloseを呼ぶだけの形も違反として検出すること()
    {
        // 案内の表示が失敗すると Close() へ到達しない（Issue #1745）
        const string body = @"
            try { await _viewModel.InitializeAsync(); }
            catch (Exception ex)
            {
                OwnedMessageBox.Show(ExceptionMessageFormatter.ToUserMessage(ex, ""初期化""));
                Close();
            }
        ";

        HasGuaranteedCloseOnFailure(body).Should().BeFalse();
    }

    [Fact]
    public void 検査ロジック_catchの中のfinallyでCloseを呼ぶ形を適合として扱うこと()
    {
        const string body = @"
            try { await _viewModel.InitializeAsync(); }
            catch (Exception ex)
            {
                try { OwnedMessageBox.Show(ExceptionMessageFormatter.ToUserMessage(ex, ""初期化"")); }
                finally { Close(); }
            }
        ";

        HasGuaranteedCloseOnFailure(body).Should().BeTrue();
    }

    [Fact]
    public void 検査ロジック_catchが1つも無い形を違反として検出すること()
    {
        // catch ごと消して無言で握りつぶす実装で緑にならないこと
        const string body = @"
            await _viewModel.InitializeAsync();
            var shouldClose = await _viewModel.StartNewCardWithIdmAsync(_presetIdm);
        ";

        HasGuaranteedCloseOnFailure(body).Should().BeFalse();
    }

    #endregion

    /// <summary>
    /// <c>*_Loaded</c> ハンドラーの本体を抽出する。
    /// </summary>
    private static string ExtractLoadedHandlerBody(string codeOnly)
    {
        var match = LoadedHandlerPattern.Match(codeOnly);
        if (!match.Success)
        {
            return string.Empty;
        }

        var braceStart = codeOnly.IndexOf('{', match.Index);
        return braceStart < 0 ? string.Empty : ExtractBlock(codeOnly, braceStart);
    }

    /// <summary>
    /// すべての <c>catch</c> ブロックが、内側の <c>finally</c> で <c>Close()</c> を呼んでいるか。
    /// <c>catch</c> が 1 つも無い場合は false（初期化失敗を受け止めていない）。
    /// </summary>
    internal static bool HasGuaranteedCloseOnFailure(string methodBody)
    {
        var catchBodies = ExtractCatchBodies(methodBody);
        if (catchBodies.Count == 0)
        {
            return false;
        }

        return catchBodies.All(catchBody =>
            ExtractFinallyBodies(catchBody).Any(f => f.Contains("Close()")));
    }

    private static IReadOnlyList<string> ExtractCatchBodies(string source) =>
        ExtractKeywordBlocks(source, "catch", allowFilterParentheses: true);

    private static IReadOnlyList<string> ExtractFinallyBodies(string source) =>
        ExtractKeywordBlocks(source, "finally", allowFilterParentheses: false);

    private static IReadOnlyList<string> ExtractKeywordBlocks(
        string source,
        string keyword,
        bool allowFilterParentheses)
    {
        var results = new List<string>();
        var pattern = new Regex($@"(?<![A-Za-z0-9_]){keyword}(?![A-Za-z0-9_])");

        foreach (Match match in pattern.Matches(source))
        {
            var index = match.Index + keyword.Length;

            // catch (Exception ex) when (...) のような括弧を読み飛ばす
            while (index < source.Length)
            {
                if (char.IsWhiteSpace(source[index]))
                {
                    index++;
                }
                else if (allowFilterParentheses && source[index] == '(')
                {
                    index = SkipParentheses(source, index);
                }
                else if (allowFilterParentheses && source.Length - index >= 4
                         && source.Substring(index, 4) == "when")
                {
                    index += 4;
                }
                else
                {
                    break;
                }
            }

            if (index < source.Length && source[index] == '{')
            {
                results.Add(ExtractBlock(source, index));
            }
        }

        return results;
    }

    private static int SkipParentheses(string source, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '(')
            {
                depth++;
            }
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }
        }

        return source.Length;
    }

    /// <summary>
    /// <paramref name="openIndex"/> の <c>{</c> に対応する <c>}</c> までの中身を返す。
    /// </summary>
    private static string ExtractBlock(string source, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }

        return string.Empty;
    }
}
