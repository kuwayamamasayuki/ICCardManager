using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// カード読み取りイベントのディスパッチが、例外を観測する経路
/// （ViewModels は <c>IDispatcherService</c>、Views は <c>DispatcherObservation</c>）を
/// 通っていることをソーステキスト上で固定する規約テスト（Issue #1843 / #1873）。
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
/// <c>ViewModels</c> / <c>Views</c> ディレクトリ配下の全 <c>.cs</c> から導出する
/// （.claude/rules/development-conventions.md Issue #1786「ガードを書くときは経路を列挙する」）。
/// </para>
/// </remarks>
public class CardReadDispatchConventionTests
{
    /// <summary>
    /// 非同期ラムダを渡す生の Dispatcher ディスパッチ。
    /// <c>Invoke</c>（同期・戻り値なし）は呼び出し元スレッドで実行され例外もそのまま伝播するため対象外。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 受け手（<c>Dispatcher</c>）を起点に照合し、<c>Application.Current.Dispatcher</c> という
    /// 特定の参照経路には依存しない。経路を直書きすると、
    /// <c>Application.Current?.Dispatcher</c>（本リポジトリでは <c>App.xaml.cs</c> が使用）や、
    /// <c>Dispatcher</c> をフィールド／プロパティへ退避した形が同じ欠陥のまま検査を素通りする
    /// （.claude/rules/development-conventions.md Issue #1786
    /// 「ガードを書くときは『守りたい性質』ではなく『その性質を破れる全経路』を列挙する」）。
    /// </para>
    /// <para>
    /// <c>BeginInvoke</c> も対象。非同期ラムダを渡すと <c>async void</c> と等価になり、
    /// 例外の観測経路が <c>InvokeAsync</c> 以上に無い。
    /// </para>
    /// <para>
    /// <c>_dispatcherService.InvokeAsync</c>（正しい形）は受け手が <c>Dispatcher</c> で
    /// 終わらないため一致しない。View 側の正しい形
    /// <c>Dispatcher.InvokeAsyncObserved</c>（Issue #1873）は受け手が <c>Dispatcher</c> だが、
    /// 末尾の語境界（<c>(?![A-Za-z0-9_])</c>）により <c>InvokeAsync</c> の前方一致では拾わない。
    /// </para>
    /// </remarks>
    private static readonly Regex RawDispatcherInvokeAsyncPattern = new(
        @"(?<![A-Za-z0-9_])Dispatcher\s*\??\s*\.\s*(?:InvokeAsync|BeginInvoke)(?![A-Za-z0-9_])",
        RegexOptions.Compiled);

    private static string ViewModelsDirectory =>
        Path.Combine(TestPaths.GetSolutionRoot(), "src", "ICCardManager", "ViewModels");

    private static string ViewsDirectory =>
        Path.Combine(TestPaths.GetSolutionRoot(), "src", "ICCardManager", "Views");

    private static IReadOnlyList<(string FileName, string CodeOnly)> LoadViewModelSources()
        => LoadSources(ViewModelsDirectory, "ViewModels");

    private static IReadOnlyList<(string FileName, string CodeOnly)> LoadViewSources()
        => LoadSources(ViewsDirectory, "Views");

    private static IReadOnlyList<(string FileName, string CodeOnly)> LoadSources(
        string directory, string label)
    {
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);

        // 走査対象が 0 件に縮んだ状態でも緑になる空振りを防ぐ
        // （.claude/rules/development-conventions.md Issue #1786）。
        files.Should().NotBeEmpty(
            $"{label} ディレクトリ（{directory}）が走査できること");

        // コメントは剥がしてから照合する。規約の理由を書いたコメント自体が違反として
        // 検出される極性の反転を避けるため（Issue #1692）。
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
    /// View コードビハインドが生の <c>Dispatcher.InvokeAsync</c> / <c>BeginInvoke</c> を
    /// 使っていないこと（Issue #1873）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Views/</c> は <c>IDispatcherService</c> を注入できないため、観測手段は
    /// <c>Common/DispatcherObservation.InvokeAsyncObserved</c> ただ 1 つに寄せる
    /// （.claude/rules/development-conventions.md Issue #1831「手段を 1 つに寄せる」）。
    /// </para>
    /// <para>
    /// Issue #1843 の時点では「広げると既存 3 件で即赤になる」ため走査対象を
    /// <c>ViewModels/</c> に限っていた。是正（Issue #1873）と同時に広げている。
    /// </para>
    /// </remarks>
    [Fact]
    public void Viewsが生のDispatcherへディスパッチしないこと()
    {
        // Act
        var violations = LoadViewSources()
            .Where(s => RawDispatcherInvokeAsyncPattern.IsMatch(s.CodeOnly))
            .Select(s => s.FileName)
            .ToList();

        // Assert
        violations.Should().BeEmpty(
            "生の Dispatcher.InvokeAsync / BeginInvoke は例外を DispatcherOperation の Task へ" +
            "格納するだけで誰も観測しない（Issue #1725 / #1873）。" +
            "Dispatcher.InvokeAsyncObserved（Common/DispatcherObservation）を使うこと。" +
            $"違反: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// カード読み取りを購読する View が、実際に観測ヘルパーを経由してディスパッチしていること
    /// </summary>
    /// <remarks>
    /// 「禁止された形の不在」だけを検査すると、ディスパッチごと消して同期実行へ倒した実装や、
    /// 走査対象が縮んだ状態でも緑になる（.claude/rules/error-messages.md Issue #1817）。
    /// </remarks>
    [Theory]
    [InlineData("StaffAuthDialog.xaml.cs")]
    public void カード読み取りを購読するViewは観測ヘルパー経由でディスパッチすること(string fileName)
    {
        // Arrange
        var source = LoadViewSources().SingleOrDefault(s => s.FileName == fileName);
        source.CodeOnly.Should().NotBeNull($"{fileName} が Views 配下に存在すること");

        // Assert
        source.CodeOnly.Should().Contain(
            "_cardReader.CardRead += OnCardRead",
            $"{fileName} がカード読み取りイベントを購読していること（購読をやめたら本検査の前提が変わる）");
        source.CodeOnly.Should().Contain(
            "Dispatcher.InvokeAsyncObserved",
            $"{fileName} の OnCardRead が観測ヘルパー経由でディスパッチすること（Issue #1873）");
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
    // 参照経路が変わっても検出すること（経路の直書きに戻さないための固定）
    [InlineData("Application.Current?.Dispatcher.InvokeAsync(() => X());", true)]
    [InlineData("Application.Current.Dispatcher?.InvokeAsync(() => X());", true)]
    [InlineData("_dispatcher.InvokeAsync(() => X());", false)]
    [InlineData("Dispatcher.InvokeAsync(async () => await X());", true)]
    [InlineData("Dispatcher.BeginInvoke(new Action(async () => await X()));", true)]
    [InlineData("_dispatcherService.InvokeAsync(() => HandleCardReadAsync(e.Idm));", false)]
    [InlineData("Application.Current.Dispatcher.Invoke(() => X());", false)]
    // View 側の正しい形（Issue #1873）。InvokeAsync の前方一致で拾わないこと
    [InlineData("Dispatcher.InvokeAsyncObserved(() => HandleCardReadAsync(e.Idm), \"職員証の認証\");", false)]
    [InlineData("dataGrid.Dispatcher.InvokeAsyncObserved(() => X(), \"再検索\", DispatcherPriority.ContextIdle);", false)]
    public void 検出パターンはサンプル入力で固定されていること(string line, bool expected)
    {
        RawDispatcherInvokeAsyncPattern.IsMatch(line).Should().Be(expected);
    }
}
