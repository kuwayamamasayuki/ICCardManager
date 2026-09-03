using FluentAssertions;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #2001: 非一時的な <c>SQLiteException</c> を <c>false</c> へ畳む catch には、
/// 必ず <c>DbContext.LogNonTransientWriteFailure</c> が併設されていることの静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 挙動テスト（<c>RepositoryInsertFailureLoggingTests</c>）はカードと職員の 2 経路を固定するが、
/// <b>3 つ目の書き込み経路が同じ catch を書き足したときの追随漏れを検出できない</b>。
/// 同じ形が経路の追加で静かに再発することは #1727 → #1759 → #1764 で 3 度起きており、
/// そのとき採った手当て（個別テストからソーステキストの静的検査へ移す）をここでも採る。
/// </para>
/// <para>
/// <b>抽出と判定は Issue #1951 の検査（<c>SQLiteBusySwallowConventionTests</c>）と共有する</b>
/// （<see cref="SQLiteCatchBlockInspection"/>）。両者は同じ構文を走査するため、
/// 目印の解釈を各テストへ書き写すと片方だけが実装のリファクタに追随する
/// （<c>.claude/rules/testing.md</c>「静的検査の下請けを私的にコピーしない」／#1763）。
/// 共有ヘルパーは型名の修飾と空白の揺れを許すので、
/// 完全修飾（<c>ICCardManager.Data.DbContext.IsTransientLockError(ex)</c>。
/// <c>ViewModels/CardManageViewModel</c> に実在する記法）でも検出できる。
/// </para>
/// <para>
/// <b>走査対象は本番ソース全体から導出する</b>。<c>LogNonTransientWriteFailure</c> は
/// <c>internal</c> で同一アセンブリのどこからでも呼べるため、
/// 畳む catch が <c>Data/</c> の外（<c>Services/</c> 等）へ生えても同じ規約が要る。
/// ファイル名でもディレクトリでも列挙しない（#1786）。
/// </para>
/// <para>
/// 入力は <see cref="TestSourceInspection.ToCodeOnlyPreservingLines"/> を通す
/// （共有ヘルパーが自ら通す）。コメントを剥がすのは、「この catch はログを併設すること」という
/// <b>規約の理由を書いたコメント自体</b>が合格判定に使われる極性の反転を避けるため（#1692）。
/// 文字列リテラルの中身まで捨てるのは、本体に含まれる波括弧でブロックの対応が狂わないようにするため（#1960）。
/// </para>
/// </remarks>
public class NonTransientWriteFailureLoggingConventionTests
{
    /// <summary>併設が要求される記録の呼び出し（型名の修飾と空白の揺れを許す）</summary>
    private const string RequiredLoggingCallPattern =
        @"(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*LogNonTransientWriteFailure\s*\(";

    /// <summary>
    /// 非一時的エラーを <c>false</c> へ畳む catch は、すべて記録を併設していること。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void 非一時的エラーを畳むcatchはすべてログを併設していること()
    {
        var violations = new List<string>();

        foreach (var (path, source) in SQLiteCatchBlockInspection.EnumerateProductionSources())
        {
            foreach (var block in FoldingCatchBodies(source))
            {
                if (!Regex.IsMatch(block, RequiredLoggingCallPattern))
                {
                    violations.Add($"{Path.GetFileName(path)}: {Summarize(block)}");
                }
            }
        }

        violations.Should().BeEmpty(
            "非一時的な失敗（ディスク満杯・DB 破損・読み取り専用）を false へ畳むと、" +
            "呼び出し元にとって理由の無い失敗になる。ログが唯一の痕跡であるため、" +
            "畳む catch には必ず DbContext.LogNonTransientWriteFailure を併設すること。違反: " +
            string.Join(" / ", violations));
    }

    /// <summary>
    /// 対の表明: 検査が実際に catch を拾えていること。
    /// </summary>
    /// <remarks>
    /// 目印が実装のリファクタでずれると抽出が 0 件になり、上のテストは<b>永久に緑</b>になる。
    /// 既知の 2 経路（カード登録・職員登録）が実際に走査対象へ入っていることを表明する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void 検査は既知の2経路を実際に走査していること()
    {
        var found = SQLiteCatchBlockInspection.EnumerateProductionSources()
            .SelectMany(x => FoldingCatchBodies(x.Source).Select(b => Path.GetFileName(x.Path)))
            .ToList();

        found.Should().HaveCountGreaterOrEqualTo(2,
            "CardRepository / StaffRepository の登録が畳む catch を持つ。" +
            "0 件になるのは目印がずれた合図であり、規約が満たされた証拠ではない");
        found.Should().Contain("CardRepository.cs");
        found.Should().Contain("StaffRepository.cs");
    }

    /// <summary>
    /// 対の表明: 判定ロジックがサンプル入力で違反と適合を区別すること。
    /// </summary>
    /// <remarks>
    /// 実データが空になっても、この表明だけは働き続ける（#1786「空振り検出を
    /// 『各対象が非空であること』で書かない」）。
    /// </remarks>
    [Theory]
    [Trait("Category", "Unit")]
    // 適合: 記録を併設している
    [InlineData(@"
        catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
        {
            DbContext.LogNonTransientWriteFailure(_logger, ex, ""交通系ICカードの登録"");
            return false;
        }", false)]
    // 違反: 痕跡なく畳む（Issue #2001 の欠陥そのもの）
    [InlineData(@"
        catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
        {
            return false;
        }", true)]
    // 違反: コメントに書いただけでは満たさない（極性の反転を防ぐ）
    [InlineData(@"
        catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
        {
            // ここでは DbContext.LogNonTransientWriteFailure( を呼ばない
            return false;
        }", true)]
    // 違反: 文字列リテラル中の記述も合格にしない
    [InlineData(@"
        catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
        {
            var note = ""DbContext.LogNonTransientWriteFailure( を呼ぶこと"";
            return false;
        }", true)]
    // 適合: 完全修飾（CardManageViewModel に実在する記法）でも検出・判定できる
    [InlineData(@"
        catch (SQLiteException ex) when (!ICCardManager.Data.DbContext.IsTransientLockError(ex))
        {
            ICCardManager.Data.DbContext.LogNonTransientWriteFailure(_logger, ex, ""職員の登録"");
            return false;
        }", false)]
    // 適合: 本体に波括弧を含むリテラルがあってもブロックの対応が狂わない（#1960）
    [InlineData(@"
        catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
        {
            _logger?.LogDebug(""SQL={Sql} escaped={{0}}"", sql);
            DbContext.LogNonTransientWriteFailure(_logger, ex, ""職員の登録"");
            return false;
        }", false)]
    public void 判定ロジックはサンプル入力で違反と適合を区別すること(string sample, bool expectViolation)
    {
        var bodies = FoldingCatchBodies(TestSourceInspection.ToCodeOnlyPreservingLines(sample));

        bodies.Should().ContainSingle("サンプルは畳む catch を 1 つだけ含む");
        Regex.IsMatch(bodies[0], RequiredLoggingCallPattern).Should().Be(!expectViolation);
    }

    /// <summary>
    /// 対の表明: 目印を持たない catch を巻き込まないこと（正当な例外変換で赤くならない）。
    /// </summary>
    /// <remarks>
    /// 誤検出はガード自体の寿命を縮める（#1786）。
    /// 一過性ロックを畳む極性反転の形は Issue #1951 の検査が違反として扱うため、
    /// ここでは<b>対象外</b>（＝本検査は記録の有無を問わない）であることを固定する。
    /// </remarks>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(@"
        catch (SQLiteException ex) when (IsDuplicateCardNumberError(ex))
        {
            throw new DuplicateCardNumberException(card.CardType, card.CardNumber, ex);
        }")]
    [InlineData(@"
        catch (SQLiteException ex) when (DbContext.IsTransientLockError(ex))
        {
            return false;
        }")]
    [InlineData(@"
        catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
        {
            throw new DatabaseException(""登録"", ex);
        }")]
    public void 畳まないcatchと極性が反転したcatchは対象外であること(string sample)
    {
        FoldingCatchBodies(TestSourceInspection.ToCodeOnlyPreservingLines(sample))
            .Should().BeEmpty();
    }

    /// <summary>
    /// 「一過性ロックを除外するフィルタを持ち、かつ <c>false</c> へ畳む」catch の本体を返す。
    /// </summary>
    private static IReadOnlyList<string> FoldingCatchBodies(string codeOnlySource)
        => SQLiteCatchBlockInspection.ExtractSQLiteCatchBlocks(codeOnlySource)
            .Where(c => SQLiteCatchBlockInspection.ExcludesTransientLockError(c.Filter)
                        && SQLiteCatchBlockInspection.SwallowsToFalse(c.Body))
            .Select(c => c.Body)
            .ToList();

    private static string Summarize(string block)
    {
        var flattened = string.Join(" ", block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return flattened.Length <= 120 ? flattened : flattened.Substring(0, 120) + "…";
    }
}
