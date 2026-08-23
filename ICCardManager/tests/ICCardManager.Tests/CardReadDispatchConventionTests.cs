using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// カード読み取りイベントのディスパッチが、例外を観測する経路（<c>IDispatcherService</c>）を
/// 通っていることをソーステキスト上で固定する規約テスト（Issue #1843）。
/// </summary>
/// <remarks>
/// <para>
/// 生の <c>Application.Current.Dispatcher.InvokeAsync(() =&gt; SomethingAsync())</c> は
/// <c>DispatcherOperation&lt;Task&gt;</c> を返すため、戻り値を await しても内側の
/// <c>Task</c> の例外は観測されない（<c>Unwrap()</c> が要る。Issue #1725）。
/// 誰も観測しないと <c>TaskScheduler.UnobservedTaskException</c> が GC 契機で
/// 遅れて発火するだけで、ログにも画面にも何も残らない。
/// </para>
/// <para>
/// Issue #1816 は「本体全体を try/catch で包む」という受け皿を各 ViewModel に置いたが、
/// これは fail-safe ではない。<c>catch</c> ブロック自身（<c>CancelEdit()</c> の
/// <c>_messenger.Send</c>、<c>StatusMessage</c> 代入の <c>PropertyChanged</c>）が投げれば
/// 再び無言になる（.claude/rules/development-conventions.md Issue #1745
/// 「catch の中の後始末は、それ自体が失敗し得ることを前提に書く」）。
/// </para>
/// <para>
/// 経路ごとの個別テストでは、新しく ViewModel が増えたときの追随漏れを検出できない
/// （.claude/rules/error-messages.md Issue #1764）。走査対象はファイル名で列挙せず、
/// <c>ViewModels</c> ディレクトリ配下の全 <c>.cs</c> から導出する
/// （.claude/rules/development-conventions.md Issue #1786「ガードを書くときは経路を列挙する」）。
/// </para>
/// </remarks>
public class CardReadDispatchConventionTests
{
    /// <summary>
    /// 非同期ラムダを渡す生の Dispatcher ディスパッチ。
    /// <c>Invoke</c>（同期・戻り値なし）は呼び出し元スレッドで実行され例外もそのまま伝播するため対象外。
    /// </summary>
    private static readonly Regex RawDispatcherInvokeAsyncPattern = new(
        @"Application\s*\.\s*Current\s*\.\s*Dispatcher\s*\.\s*InvokeAsync",
        RegexOptions.Compiled);

    private static string ViewModelsDirectory =>
        Path.Combine(TestPaths.GetSolutionRoot(), "src", "ICCardManager", "ViewModels");

    private static IReadOnlyList<(string FileName, string CodeOnly)> LoadViewModelSources()
    {
        var files = Directory.GetFiles(ViewModelsDirectory, "*.cs", SearchOption.AllDirectories);

        // 走査対象が 0 件に縮んだ状態でも緑になる空振りを防ぐ
        // （.claude/rules/development-conventions.md Issue #1786）。
        files.Should().NotBeEmpty(
            $"ViewModels ディレクトリ（{ViewModelsDirectory}）が走査できること");

        return files
            .Select(f => (Path.GetFileName(f), TestSourceInspection.ToCodeOnly(File.ReadAllText(f))))
            .ToList();
    }

    /// <summary>
    /// ViewModel が生の <c>Application.Current.Dispatcher.InvokeAsync</c> を使っていないこと
    /// </summary>
    [Fact]
    public void ViewModels_生のDispatcherへ非同期ラムダをディスパッチしないこと()
    {
        // Act
        var violations = LoadViewModelSources()
            .Where(s => RawDispatcherInvokeAsyncPattern.IsMatch(s.CodeOnly))
            .Select(s => s.FileName)
            .ToList();

        // Assert
        violations.Should().BeEmpty(
            "生の Dispatcher.InvokeAsync は DispatcherOperation<Task> を返すため内側の Task の例外を" +
            "観測できない（Issue #1725 / #1843）。IDispatcherService.InvokeAsync を使うこと。" +
            $"違反: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// カードリーダーイベントを購読する ViewModel が、実際に <c>IDispatcherService</c> を
    /// 経由してディスパッチしていること（「禁止された形の不在」と対で「正しい形の存在」を表明する）
    /// </summary>
    /// <remarks>
    /// 不在だけを検査すると、ディスパッチごと消して同期実行へ倒した実装や、
    /// 走査対象が縮んだ状態でも緑になる
    /// （.claude/rules/error-messages.md Issue #1817）。
    /// </remarks>
    [Theory]
    [InlineData("StaffManageViewModel.cs")]
    [InlineData("CardManageViewModel.cs")]
    [InlineData("DataExportImportViewModel.cs")]
    public void カード読み取りを購読するViewModelはIDispatcherService経由でディスパッチすること(string fileName)
    {
        // Arrange
        var source = LoadViewModelSources().SingleOrDefault(s => s.FileName == fileName);
        source.CodeOnly.Should().NotBeNull($"{fileName} が ViewModels 配下に存在すること");

        // Assert
        source.CodeOnly.Should().Contain(
            "_cardReader.CardRead += OnCardRead",
            $"{fileName} がカード読み取りイベントを購読していること（購読をやめたら本検査の前提が変わる）");
        source.CodeOnly.Should().Contain(
            "_dispatcherService.InvokeAsync",
            $"{fileName} の OnCardRead が IDispatcherService 経由でディスパッチすること（Issue #1843）");
    }

    /// <summary>
    /// 検出パターンが「生の Dispatcher.InvokeAsync だけ」を拾い、正しい形を誤検出しないこと
    /// </summary>
    /// <remarks>
    /// 検査ロジック自体を既知のサンプル入力で固定する。実データが空になっても
    /// 空振り検出が働き続けるようにするため
    /// （.claude/rules/development-conventions.md Issue #1786）。
    /// </remarks>
    [Theory]
    [InlineData("System.Windows.Application.Current.Dispatcher.InvokeAsync(() => X());", true)]
    [InlineData("Application.Current.Dispatcher.InvokeAsync(async () => await X());", true)]
    [InlineData("_dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));", false)]
    [InlineData("Application.Current.Dispatcher.Invoke(() => X());", false)]
    public void 検出パターンはサンプル入力で固定されていること(string line, bool expected)
    {
        RawDispatcherInvokeAsyncPattern.IsMatch(line).Should().Be(expected);
    }
}
