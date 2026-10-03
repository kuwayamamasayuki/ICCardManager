using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerDetailViewModelの単体テスト
/// Issue #633: 分割操作でGroupIdが正しく設定されることを検証
/// Issue #1134: パンくず表示を検証
/// </summary>
public class LedgerDetailViewModelTests : IDisposable
{
    private readonly LedgerDetailViewModel _viewModel;
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly DbContext _dbContext;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;
    private readonly List<OperationLog> _operationLogs = new();

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    public LedgerDetailViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _dbContext = TestDbContextFactory.Create();
        var summaryGenerator = new SummaryGenerator();
        var operationLogRepoMock = new Mock<IOperationLogRepository>();
        // Issue #1760: ログの中身はログ記録クラスのモックでは表明できないため、書き込み先で捕捉する
        operationLogRepoMock
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(1)
            .Callback((OperationLog log, System.Data.SQLite.SQLiteTransaction _) => _operationLogs.Add(log));
        var staffRepoMock = new Mock<IStaffRepository>();
        var operationLogger = new OperationLogger(
            operationLogRepoMock.Object,
            Mock.Of<ICurrentOperatorContext>());
        var splitServiceLogger = NullLogger<LedgerSplitService>.Instance;
        var ledgerSplitService = new LedgerSplitService(
            _ledgerRepoMock.Object,
            summaryGenerator,
            operationLogger,
            _dbContext,
            splitServiceLogger);
        var logger = NullLogger<LedgerDetailViewModel>.Instance;
        _staffAuthServiceMock = new Mock<IStaffAuthService>();

        _viewModel = new LedgerDetailViewModel(
            _ledgerRepoMock.Object,
            summaryGenerator,
            new LedgerDetailSaveService(
                _dbContext, _ledgerRepoMock.Object, operationLogger, NullLogger<LedgerDetailSaveService>.Instance),
            ledgerSplitService,
            _staffAuthServiceMock.Object,
            logger);
    }

    /// <summary>
    /// テスト用にItemsを直接追加するヘルパー
    /// </summary>
    private void AddItems(int count)
    {
        _viewModel.Items.Clear();
        for (int i = 0; i < count; i++)
        {
            var detail = new LedgerDetail
            {
                EntryStation = $"駅{i * 2 + 1}",
                ExitStation = $"駅{i * 2 + 2}",
                UseDate = new DateTime(2026, 2, 10, 10 + i, 0, 0),
                Balance = 1000 - (i * 260),
                Amount = 260,
                SequenceNumber = i + 1
            };
            _viewModel.Items.Add(new LedgerDetailItemViewModel(detail, i));
        }
    }

    #region ToggleDividerAt テスト

    [Fact]
    public void ToggleDividerAt_TwoItems_BothGetGroupId()
    {
        // Arrange
        AddItems(2);

        // Act: 1番目のアイテムの下に分割線を挿入
        _viewModel.ToggleDividerAt(0);

        // Assert: 分割線があるため、両方にGroupIdが設定される
        _viewModel.Items[0].GroupId.Should().NotBeNull("分割線がある場合、単独アイテムにもGroupIdが付与される");
        _viewModel.Items[1].GroupId.Should().NotBeNull("分割線がある場合、単独アイテムにもGroupIdが付与される");
        _viewModel.Items[0].GroupId.Should().NotBe(_viewModel.Items[1].GroupId,
            "分割されたアイテムは異なるGroupIdを持つ");
    }

    [Fact]
    public void ToggleDividerAt_ThreeItems_SplitAfterFirst_CorrectGroupIds()
    {
        // Arrange
        AddItems(3);

        // Act: 1番目のアイテムの下に分割線を挿入
        _viewModel.ToggleDividerAt(0);

        // Assert
        // Item 0: GroupId=1（単独グループ）
        _viewModel.Items[0].GroupId.Should().Be(1, "1番目のアイテムは独立したグループ");
        // Item 1, 2: GroupId=2（同じグループ）
        _viewModel.Items[1].GroupId.Should().Be(2, "2番目と3番目は同じグループ");
        _viewModel.Items[2].GroupId.Should().Be(2, "2番目と3番目は同じグループ");
    }

    /// <summary>
    /// Issue #1816: 分割線を外して 1 グループに戻しても、自動検出モードへは戻さない
    /// </summary>
    /// <remarks>
    /// 修正前は「分割線なし」を自動検出（GroupId=null）として扱っていたため、
    /// 「すべて統合」の直後に分割線を 1 回 ON→OFF しただけで統合が黙って取り消され、
    /// 画面の見た目（分割線なし）は同じまま保存時の摘要だけが分かれた。
    /// 自動検出へ戻す経路は `ResetToAutoDetect` だけにする。
    /// </remarks>
    [Fact]
    public void ToggleDividerAt_Toggle_RemovesDivider_KeepsSingleGroup()
    {
        // Arrange
        AddItems(2);
        _viewModel.ToggleDividerAt(0); // 分割線を挿入

        // Act: もう一度トグルして分割線を削除
        _viewModel.ToggleDividerAt(0);

        // Assert: 1つのグループとして明示される（自動検出へは戻さない）
        _viewModel.Items.Should().OnlyContain(
            i => i.GroupId == LedgerDetailViewModel.MergedGroupId,
            "分割線なしは「利用者が指定した単一グループ」として扱う");
        _viewModel.HasMultipleGroups.Should().BeFalse();
    }

    /// <summary>
    /// Issue #1816: 「すべて統合」のあとに分割線を往復させても統合が取り消されないこと
    /// </summary>
    [Fact]
    public void MergeAll_ThenToggleDividerRoundTrip_KeepsMergedGroup()
    {
        // Arrange
        AddItems(3);
        _viewModel.MergeAllCommand.Execute(null);

        // Act: 分割線を入れて、すぐ外す（画面上は統合直後と同じ見た目に戻る）
        _viewModel.ToggleDividerAt(0);
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.Items.Should().OnlyContain(
            i => i.GroupId == LedgerDetailViewModel.MergedGroupId,
            "見た目が統合直後と同じなら、保存される内容も同じであること");
        _viewModel.DetailCountDisplay.Should().Be("3件の詳細（1グループ）");
    }

    #endregion

    #region SplitAll テスト

    [Fact]
    public void SplitAll_ThreeItems_AllGetUniqueGroupIds()
    {
        // Arrange
        AddItems(3);

        // Act
        _viewModel.SplitAllCommand.Execute(null);

        // Assert: 全アイテムにGroupIdが付与される
        _viewModel.Items[0].GroupId.Should().Be(1);
        _viewModel.Items[1].GroupId.Should().Be(2);
        _viewModel.Items[2].GroupId.Should().Be(3);
    }

    [Fact]
    public void SplitAll_TwoItems_BothGetDistinctGroupIds()
    {
        // Arrange
        AddItems(2);

        // Act
        _viewModel.SplitAllCommand.Execute(null);

        // Assert
        _viewModel.Items[0].GroupId.Should().NotBeNull();
        _viewModel.Items[1].GroupId.Should().NotBeNull();
        _viewModel.Items[0].GroupId.Should().NotBe(_viewModel.Items[1].GroupId);
    }

    #endregion

    #region MergeAll テスト

    /// <summary>
    /// Issue #1816: 「すべて統合」は全項目に同一の GroupId を明示付与する。
    /// </summary>
    /// <remarks>
    /// 修正前は分割線を消して <c>RecalculateGroupsFromDividers()</c> を呼ぶだけで、
    /// 「分割線なし＝自動検出」の分岐に落ちて GroupId が null になっていた
    /// （＝「自動検出に戻す」と同一動作）。この表明は両者の区別を固定する。
    /// </remarks>
    [Fact]
    public void MergeAll_AfterSplit_AssignsSingleGroupIdToAllItems()
    {
        // Arrange
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        // すべてに別々のGroupIdが設定されていることを確認
        _viewModel.Items.Select(i => i.GroupId).Distinct().Should().HaveCount(3);

        // Act: すべてを統合
        _viewModel.MergeAllCommand.Execute(null);

        // Assert: 全項目が同一の非nullグループになる（自動検出モードには戻さない）
        _viewModel.Items.Should().OnlyContain(
            i => i.GroupId == LedgerDetailViewModel.MergedGroupId,
            "統合後は「利用者が1グループを指定した」ことをGroupIdで表す");
        _viewModel.Items.Should().OnlyContain(i => !i.ShowDividerBelow, "分割線はすべて削除される");
    }

    /// <summary>
    /// Issue #1816: 統合後は件数表示が「1グループ」を示す（対のテスト: 自動検出はグループ表示なし）。
    /// </summary>
    [Fact]
    public void MergeAll_DetailCountDisplay_ShowsSingleGroup()
    {
        // Arrange
        AddItems(3);

        // Act
        _viewModel.MergeAllCommand.Execute(null);

        // Assert
        _viewModel.DetailCountDisplay.Should().Be("3件の詳細（1グループ）");
    }

    /// <summary>
    /// Issue #1816: 「自動検出に戻す」は従来どおり GroupId を null にする（統合と区別する）。
    /// </summary>
    [Fact]
    public void ResetToAutoDetect_AfterMergeAll_ClearsAllGroupIds()
    {
        // Arrange
        AddItems(3);
        _viewModel.MergeAllCommand.Execute(null);
        _viewModel.Items.Should().OnlyContain(i => i.GroupId.HasValue);

        // Act
        _viewModel.ResetToAutoDetectCommand.Execute(null);

        // Assert
        _viewModel.Items.Should().OnlyContain(i => i.GroupId == null, "自動検出モードはGroupIdなし");
        _viewModel.DetailCountDisplay.Should().Be("3件の詳細");
    }

    #endregion

    #region HasChanges テスト

    [Fact]
    public void ToggleDividerAt_SetsHasChanges()
    {
        // Arrange
        AddItems(2);
        _viewModel.HasChanges.Should().BeFalse();

        // Act
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
    }

    #endregion

    #region Issue #634: HasMultipleGroups テスト

    [Fact]
    public void ToggleDividerAt_TwoItems_HasMultipleGroupsIsTrue()
    {
        // Arrange
        AddItems(2);
        _viewModel.HasMultipleGroups.Should().BeFalse("初期状態ではfalse");

        // Act: 分割線を挿入して2グループにする
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.HasMultipleGroups.Should().BeTrue("2グループある場合はtrue");
    }

    [Fact]
    public void MergeAll_HasMultipleGroupsIsFalse()
    {
        // Arrange: まず分割してMultipleGroupsをtrueにする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasMultipleGroups.Should().BeTrue();

        // Act: すべて統合
        _viewModel.MergeAllCommand.Execute(null);

        // Assert
        _viewModel.HasMultipleGroups.Should().BeFalse("統合後はfalse");
    }

    [Fact]
    public void ToggleDividerAt_RemoveDivider_HasMultipleGroupsReturnsFalse()
    {
        // Arrange
        AddItems(2);
        _viewModel.ToggleDividerAt(0);
        _viewModel.HasMultipleGroups.Should().BeTrue();

        // Act: 分割線を削除
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.HasMultipleGroups.Should().BeFalse("分割線を外すとfalseに戻る");
    }

    #endregion

    #region Issue #1134: パンくず表示テスト

    [Fact]
    public async Task InitializeAsync_カード名ありの場合パンくずが設定されること()
    {
        // Arrange
        var testLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 3, 15),
            Summary = "鉄道（博多駅～天神駅）",
            Balance = 500,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { SequenceNumber = 1, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 500 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(testLedger);

        // Act
        await _viewModel.InitializeAsync(1, cardName: "nimoca N-002");

        // Assert
        _viewModel.BreadcrumbText.Should().Be("nimoca N-002 > 履歴詳細");
    }

    [Fact]
    public async Task InitializeAsync_カード名なしの場合パンくずがデフォルト設定されること()
    {
        // Arrange
        var testLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 3, 15),
            Summary = "テスト",
            Balance = 500,
            Details = new List<LedgerDetail>()
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(testLedger);

        // Act
        await _viewModel.InitializeAsync(1);

        // Assert
        _viewModel.BreadcrumbText.Should().Be("履歴詳細");
    }

    #endregion

    #region 履歴分割の職員認証ゲート（SEQ-AUTH-01）

    [Fact]
    public async Task SaveWithFullSplit_認証キャンセル時_分割せず中止メッセージを表示する()
    {
        // Arrange: 3項目を個別グループに分割し、変更あり状態にする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasChanges.Should().BeTrue();
        // _staffAuthServiceMock は未設定 → RequestAuthenticationAsync は既定で null（=認証キャンセル）を返す

        // Act
        await _viewModel.SaveWithFullSplitCommand.ExecuteAsync(null);

        // Assert: 認証を要求し、キャンセルされたため分割を実行しない
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync("履歴の分割"), Times.Once);
        _viewModel.HasChanges.Should().BeTrue(
            "認証キャンセル時は分割を実行しないため変更フラグは保持される");
        _viewModel.StatusMessage.Should().Be("認証がキャンセルされたため分割を中止しました");
    }

    #endregion

    #region ダイアログクローズガード（Issue #1743）

    [Fact]
    public void CanClose_変更がなければ確認を求めずに閉じられる()
    {
        // Arrange
        var confirmCalls = 0;
        _viewModel.HasChanges.Should().BeFalse("前提: 初期状態は未保存の変更なし");

        // Act
        var canClose = _viewModel.CanClose(() => { confirmCalls++; return false; });

        // Assert
        canClose.Should().BeTrue("未保存の変更が無ければ破棄確認なしで閉じられるべき");
        confirmCalls.Should().Be(0, "変更が無いのに破棄確認を出すと操作の妨げになる");
    }

    [Fact]
    public void CanClose_変更ありで破棄を承諾すると閉じられる()
    {
        // Arrange: 分割操作で未保存の変更がある状態にする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasChanges.Should().BeTrue("前提: 分割操作で未保存の変更がある状態");
        var confirmCalls = 0;

        // Act
        var canClose = _viewModel.CanClose(() => { confirmCalls++; return true; });

        // Assert
        canClose.Should().BeTrue("破棄を承諾したら閉じられるべき");
        confirmCalls.Should().Be(1, "未保存の変更があるときは必ず破棄確認を通す");
    }

    [Fact]
    public void CanClose_変更ありで破棄を拒否すると閉じられない()
    {
        // Arrange: 分割操作で未保存の変更がある状態にする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasChanges.Should().BeTrue("前提: 分割操作で未保存の変更がある状態");

        // Act
        var canClose = _viewModel.CanClose(() => false);

        // Assert
        canClose.Should().BeFalse("「いいえ」を選んだら編集内容を保持したままダイアログに留まるべき");
        _viewModel.HasChanges.Should().BeTrue("拒否しても編集内容（変更フラグ）は失われない");
    }

    [Fact]
    public async Task CanClose_保存トランザクション実行中は確認を求めず閉じられない()
    {
        // Arrange: 実際に保存を走らせ、ReplaceDetailsAsync の中で止めて「保存中」を再現する
        //（IsBusy を直接立てると本番で成立しない状態を検証してしまう）
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        var replaceEntered = new TaskCompletionSource<bool>();
        var releaseReplace = new TaskCompletionSource<bool>();
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Returns(async () =>
            {
                replaceEntered.TrySetResult(true);
                await releaseReplace.Task;
                return true;
            });
        _ledgerRepoMock.Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>())).ReturnsAsync(true);

        var saveTask = _viewModel.SaveCommand.ExecuteAsync(null);
        await WaitForAsync(replaceEntered, "ReplaceDetailsAsync の開始");
        _viewModel.IsBusy.Should().BeTrue("前提: 保存トランザクションが実行中");

        try
        {
            var confirmCalls = 0;

            // Act: 保存中に ✕ / Alt+F4 / Escape で閉じようとする
            var canClose = _viewModel.CanClose(() => { confirmCalls++; return true; });

            // Assert: DB コミット中にウィンドウが閉じると、保存は成功しているのに
            // 呼び出し元が WasSaved=false を見て履歴一覧を再読込しない
            canClose.Should().BeFalse("保存トランザクションの実行中は閉じられないこと");
            confirmCalls.Should().Be(0,
                "保存中の変更は破棄されるわけではないため、破棄確認を出すのは事実に反する");
        }
        finally
        {
            releaseReplace.TrySetResult(true);
            await saveTask;
        }
    }

    /// <summary>
    /// Issue #2177: 保存で競合（この履歴が他 PC で削除・統合された）を検出したら、何も保存されない。
    /// 保存し直しても必ず失敗するので未保存の変更として残さず、閉じたら一覧を読み込み直す（#1743 の扱いの変更）。
    /// </summary>
    /// <remarks>
    /// 以前は明細の置換だけが先に確定していたため、この状況を「明細は保存済み」として HasChanges を下ろしていた。
    /// 巻き戻り自体（DB の明細が元のまま）は実 DB の <c>LedgerDetailSaveServiceTests</c> が表明する。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_競合したとき_何も保存されず閉じたら一覧を読み込み直すこと()
    {
        // Arrange: 摘要 UPDATE が競合で 0 行（共有モードで他 PC が統合・削除）
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>())).ReturnsAsync(false);
        var confirmCalls = 0;

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        // この履歴の行はもう無く、何度保存しても同じ失敗になるので、編集内容を「未保存の変更」として残さない
        // （残すと閉じるときに破棄の確認が出て、「いいえ」で保存し直しても必ず失敗する。コードレビューで検出）
        _viewModel.HasChanges.Should().BeFalse("保存できない編集内容を未保存の変更として残さない");
        _viewModel.HasPersistedChanges.Should().BeFalse("何も確定していない");
        _viewModel.CanClose(() => { confirmCalls++; return false; }).Should().BeTrue(
            "破棄の確認を出さずに閉じられる（閉じたら一覧を読み込み直す）");
        confirmCalls.Should().Be(0);
        _viewModel.StatusMessage.Should().Contain("保存できませんでした")
            .And.Contain("編集内容は保存できません")
            .And.Contain("削除されたか、他の履歴へ統合された可能性");
        _viewModel.NeedsHistoryReload.Should().BeTrue(
            "一覧に残っている行はもう DB と一致しないので、閉じたら一覧を読み込み直す（#1759）");
        _operationLogs.Should().BeEmpty("起きていない変更を監査ログに残さない");
    }

    /// <summary>
    /// Issue #2177（コードレビューで検出）: 保存に失敗したら、ViewModel はインメモリの摘要を元へ戻すこと。
    /// 戻さないと、もう一度保存したときに「摘要が変わった」と判定されず、明細だけが置き換わって
    /// 摘要の UPDATE が行われない（本 Issue の食い違いが再保存の経路で再発する）。
    /// 再保存できる失敗（摘要の更新の例外／明細の置換が反映されない）の両方で固定する。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsync_保存に失敗したあと再保存すると新しい摘要でUPDATEされること(bool failByUpdateException)
    {
        // Arrange: 摘要が変わる保存（テスト用の台帳の摘要「テスト」→ 分割後の生成値）
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        var replaceAttempt = 0;
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Returns(() => Task.FromResult(!(++replaceAttempt == 1 && !failByUpdateException)));
        var updatedSummaries = new List<string>();
        var updateAttempt = 0;
        _ledgerRepoMock
            .Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Returns<int, string, System.Data.SQLite.SQLiteTransaction>((_, summary, _) =>
            {
                updatedSummaries.Add(summary);
                return ++updateAttempt == 1 && failByUpdateException
                    ? Task.FromException<bool>(new InvalidOperationException("injected"))
                    : Task.FromResult(true);
            });

        // Act 1: 1 回目は失敗する
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert 1: 何も保存されず、未保存の変更として残る（閉じようとすれば破棄の確認が出る）
        _viewModel.HasChanges.Should().BeTrue(_viewModel.StatusMessage);
        _viewModel.HasPersistedChanges.Should().BeFalse();
        _viewModel.NeedsHistoryReload.Should().BeFalse("一過性の失敗では DB は変わっておらず、一覧の再読込は要らない");
        var confirmCalls = 0;
        _viewModel.CanClose(() => { confirmCalls++; return false; }).Should().BeFalse();
        confirmCalls.Should().Be(1);
        _viewModel.SummaryDisplay.Should().Be("テスト", "失敗したので画面の摘要も元のまま");

        // Act 2: もう一度保存する
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert 2: 再保存でも摘要の UPDATE が行われ、新しい摘要が書かれる
        updatedSummaries.Should().NotBeEmpty("再保存でも「摘要が変わった」と判定され、摘要の UPDATE が行われること");
        updatedSummaries.Last().Should().NotBe("テスト");
        updatedSummaries.Distinct().Should().ContainSingle("1 回目と 2 回目で同じ新しい摘要を書く");
        _viewModel.HasChanges.Should().BeFalse(_viewModel.StatusMessage);
    }

    /// <summary>
    /// Issue #2177（コードレビューで検出）: 保存に失敗したあとの再保存でも、監査ログの「変更前」は DB に確定している
    /// 状態（編集前の明細）を写すこと。画面の明細は _ledger.Details と同じインスタンスで保存のたびに GroupId を
    /// 書き換えるため、再保存で _ledger から「変更前」を採ると変更前と変更後が同じになる（#1979 の再発）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_失敗のあとの再保存でも監査ログの変更前は編集前の明細を記録すること()
    {
        // Arrange: グループ未設定（自動判定）の 2 明細。全統合すると摘要が変わる
        var ledger = new Ledger
        {
            Id = 23,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 2, 10),
            Summary = "鉄道（博多～天神、薬院～大橋）",
            Expense = 470,
            Balance = 530,
            Details = new List<LedgerDetail>
            {
                new() { LedgerId = 23, SequenceNumber = 1, EntryStation = "薬院", ExitStation = "大橋", Amount = 210, UseDate = new DateTime(2026, 2, 10), Balance = 530 },
                new() { LedgerId = 23, SequenceNumber = 2, EntryStation = "博多", ExitStation = "天神", Amount = 260, UseDate = new DateTime(2026, 2, 10), Balance = 740 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(23)).ReturnsAsync(ledger);
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(23, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);
        var attempt = 0;
        _ledgerRepoMock
            .Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Returns(() => ++attempt == 1
                ? Task.FromException<bool>(new InvalidOperationException("injected"))
                : Task.FromResult(true));
        await _viewModel.InitializeAsync(23);
        _viewModel.MergeAllCommand.Execute(null);

        // Act: 1 回目は失敗し、2 回目で保存が確定する
        await _viewModel.SaveCommand.ExecuteAsync(null);
        _operationLogs.Should().BeEmpty("前提: 1 回目は何も記録されていない");
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert: 2 回目の監査ログの変更前は編集前（グループ未設定）、変更後は統合後のグループ
        var log = _operationLogs.Should().ContainSingle(l => l.Action == "UPDATE").Subject;
        ParseDetails(log.BeforeData).EnumerateArray().Should().OnlyContain(
            d => d.GetProperty("GroupId").ValueKind == JsonValueKind.Null,
            "変更前は DB に確定している状態（1 回目の保存で書き換えた GroupId を写さない）");
        ParseDetails(log.AfterData).EnumerateArray().Should().OnlyContain(
            d => d.GetProperty("GroupId").GetInt32() == LedgerDetailViewModel.MergedGroupId);
    }

    /// <summary>
    /// Issue #2177: 摘要が変わらない保存（グループ分けだけの変更）でも、監査ログを記録すること。
    /// 以前は摘要が変わったとき、かつ操作者 IDm が渡されたときだけ記録しており、画面は操作者を渡していなかったため
    /// 明細の保存は監査ログに一度も残っていなかった。
    /// </summary>
    [Fact]
    public async Task SaveAsync_摘要が変わらない保存でも監査ログを記録すること()
    {
        // Arrange: つながらない 2 区間。分割しても摘要は「鉄道（博多～天神、薬院～大橋）」のまま変わらない
        var ledger = new Ledger
        {
            Id = 22,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 2, 10),
            Summary = "鉄道（博多～天神、薬院～大橋）",
            Expense = 470,
            Balance = 530,
            Details = new List<LedgerDetail>
            {
                new() { LedgerId = 22, SequenceNumber = 1, EntryStation = "薬院", ExitStation = "大橋", Amount = 210, UseDate = new DateTime(2026, 2, 10), Balance = 530 },
                new() { LedgerId = 22, SequenceNumber = 2, EntryStation = "博多", ExitStation = "天神", Amount = 260, UseDate = new DateTime(2026, 2, 10), Balance = 740 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(22)).ReturnsAsync(ledger);
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(22, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);
        await _viewModel.InitializeAsync(22);
        _viewModel.ToggleDividerAt(0);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.HasChanges.Should().BeFalse(_viewModel.StatusMessage);
        _ledgerRepoMock.Verify(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()), Times.Never,
            "前提: 摘要が変わらない保存であること（摘要の UPDATE を行わない）");
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()), Times.Never);
        _operationLogs.Should().ContainSingle(l => l.Action == "UPDATE", "明細の保存は摘要が変わらなくても監査ログに残す");
    }

    [Fact]
    public async Task SaveAsync_保存前はHasPersistedChangesが立たない()
    {
        // Arrange: 明細の置換自体が失敗した場合は何もコミットされていない
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(false);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.HasPersistedChanges.Should().BeFalse(
            "1 行もコミットされていないのに再読込を促すと、成功したように見える");
        _viewModel.HasChanges.Should().BeTrue("保存できていないので変更は保持される");
    }

    /// <summary>
    /// Issue #2192: 保存完了の通知（<see cref="LedgerDetailViewModel.OnSaveCompleted"/>）を受けた View は、
    /// 複数グループの保存なら <c>Close()</c> を呼ぶ（#634）。その時点で <see cref="LedgerDetailViewModel.IsBusy"/> が
    /// true のままだと、<see cref="LedgerDetailViewModel.CanClose"/>（#1743）が閉じるのを止め、保存は成功しているのに
    /// ダイアログが開いたまま残っていた（UI テストで検出）。通知の時点で「閉じてよい状態」になっていることを固定する。
    /// </summary>
    [Fact]
    public async Task SaveAsync_保存完了の通知時点では処理中が解除され確認なしに閉じられること()
    {
        // Arrange: 複数グループに分けて保存する（View が自動で閉じる経路）
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasMultipleGroups.Should().BeTrue("前提: View が保存後に自動で閉じる複数グループの保存であること");

        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>())).ReturnsAsync(true);

        var notified = 0;
        bool? busyAtNotification = null;
        bool? canCloseAtNotification = null;
        var confirmCalls = 0;
        _viewModel.OnSaveCompleted = () =>
        {
            notified++;
            busyAtNotification = _viewModel.IsBusy;
            // View の OnSaveCompleted は Close() を呼び、OnClosing が CanClose で判定する。同じ判定をここで行う
            canCloseAtNotification = _viewModel.CanClose(() => { confirmCalls++; return false; });
        };

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        notified.Should().Be(1, "保存が成功したら完了を 1 回通知すること");
        busyAtNotification.Should().BeFalse("完了を通知する時点で処理中は解除されていること");
        canCloseAtNotification.Should().BeTrue("完了の通知を受けて閉じようとしたら、閉じられること（#634 の自動クローズ）");
        confirmCalls.Should().Be(0, "保存した変更を「破棄しますか」と尋ねないこと（#1743）");
    }

    /// <summary>
    /// 対: 保存に失敗したら完了を通知しない（失敗したのにダイアログが閉じて、利用者が結果を確かめられなくなる）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_保存に失敗したら完了を通知しないこと()
    {
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(false);
        var notified = 0;
        _viewModel.OnSaveCompleted = () => notified++;

        await _viewModel.SaveCommand.ExecuteAsync(null);

        notified.Should().Be(0, "保存に失敗したら完了を通知しないこと");
        _viewModel.IsBusy.Should().BeFalse("失敗しても処理中は解除されること");
    }

    /// <summary>
    /// SaveAsync を走らせるための最小の台帳で ViewModel を初期化する。
    /// </summary>
    private async Task InitializeWithTestLedgerAsync()
    {
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2026, 2, 10),
            Summary = "テスト",
            Balance = 500,
            Details = new List<LedgerDetail>()
        });

        await _viewModel.InitializeAsync(1);
    }

    /// <summary>
    /// 非同期処理が所定の地点へ到達するのを待つ（固定時間の待機はマシン速度で不安定になる）。
    /// </summary>
    private static async Task WaitForAsync(TaskCompletionSource<bool> signal, string what)
    {
        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(signal.Task, $"{what} が 5 秒以内に到達すること");
    }

    [Fact]
    public void RequestCloseCommand_OnCloseRequestedコールバックを呼ぶ()
    {
        // Arrange
        var closeRequests = 0;
        _viewModel.OnCloseRequested = () => closeRequests++;

        // Act: Escape キーの KeyBinding から実行される経路
        _viewModel.RequestCloseCommand.Execute(null);

        // Assert: View 側で OnCloseRequested = Close を設定するため、
        // この経路が Window.Close() → OnClosing の破棄確認へ届く
        closeRequests.Should().Be(1, "Escape キーからウィンドウの Close() へ届く唯一の経路");
    }

    #endregion

    #region Issue #1816: 統合したグループが保存経路まで届くこと

    /// <summary>
    /// 「すべて統合」で付けた GroupId が <c>ReplaceDetailsAsync</c> まで届き、摘要も畳まれること
    /// </summary>
    /// <remarks>
    /// Issue #1816: ViewModel の <c>Items</c> だけを見るテストでは、保存経路が GroupId を落としても緑になる。
    /// 「画面で統合した内容が DB へ渡る」ことと「その内容から再生成される摘要」を同じテストで固定する。
    /// </remarks>
    [Fact]
    public async Task SaveAsync_MergeAll後_統合したGroupIdと畳んだ摘要が保存されること()
    {
        // Arrange: 乗り継ぎでも往復でもない 2 区間（自動検出では 2 区間に分かれる）
        var ledger = new Ledger
        {
            Id = 7,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 2, 10),
            Summary = "鉄道（博多～天神、薬院～大橋）",
            Expense = 470,
            Balance = 530,
            Details = new List<LedgerDetail>
            {
                // FeliCa 互換: 小さい SequenceNumber が新しい利用（＝薬院～大橋があと）
                new() { LedgerId = 7, SequenceNumber = 1, EntryStation = "薬院", ExitStation = "大橋", Amount = 210, UseDate = new DateTime(2026, 2, 10), Balance = 530 },
                new() { LedgerId = 7, SequenceNumber = 2, EntryStation = "博多", ExitStation = "天神", Amount = 260, UseDate = new DateTime(2026, 2, 10), Balance = 740 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(ledger);

        List<LedgerDetail>? savedDetails = null;
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(7, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Callback<int, IEnumerable<LedgerDetail>, System.Data.SQLite.SQLiteTransaction>((_, details, _) => savedDetails = details.ToList())
            .ReturnsAsync(true);

        string? savedSummary = null;
        _ledgerRepoMock
            .Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Callback<int, string, System.Data.SQLite.SQLiteTransaction>((_, summary, _) => savedSummary = summary)
            .ReturnsAsync(true);

        await _viewModel.InitializeAsync(7);

        // Act
        _viewModel.MergeAllCommand.Execute(null);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        savedDetails.Should().NotBeNull("統合内容は明細の置換として保存される");
        savedDetails!.Should().OnlyContain(
            d => d.GroupId == LedgerDetailViewModel.MergedGroupId,
            "画面で指定した単一グループが DB まで届くこと");
        savedSummary.Should().NotBeNull();
        savedSummary.Should().Be(
            "鉄道（博多～大橋）",
            "明示グループの摘要は 1 区間へ畳まれること");
    }

    #endregion

    #region Issue #1913: 保存時の明細は新しい順で渡すこと

    /// <summary>
    /// 保存時、明細は「新しい順」で <c>ReplaceDetailsAsync</c> へ渡されること
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1913: <c>ReplaceDetailsAsync</c> は DELETE + INSERT で rowid を再採番し、
    /// 渡された順にそのまま INSERT する。<c>Items</c> は時系列昇順（古い→新しい）なので、
    /// そのまま渡すと <c>LedgerDetail.SequenceNumber</c> の規約
    /// （FeliCa 互換で<b>小さい rowid ＝ 新しい</b>）が反転する。
    /// </para>
    /// <para>
    /// 反転すると、再読込後の <c>SummaryGenerator.SortChronologically</c>（同一日付内は
    /// SequenceNumber 降順がタイブレーク）が逆順を返し、摘要のブロック順とバス停名の
    /// 対応付け（Issue #1904）が崩れる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveAsync_明細は新しい順でReplaceDetailsAsyncへ渡されること()
    {
        // Arrange: 同一日付の 3 区間（順序の決定要因を SequenceNumber だけに絞る）
        var sameDay = new DateTime(2026, 2, 10);
        var ledger = new Ledger
        {
            Id = 11,
            CardIdm = "0102030405060708",
            Date = sameDay,
            // 保存で摘要が再生成される（＝UpdateSummaryAsync が呼ばれる）よう、生成結果と異なる値にしておく
            Summary = "鉄道",
            Details = new List<LedgerDetail>
            {
                // GetByIdAsync は時系列昇順（古い→新しい）で返す。
                // FeliCa 互換のため SequenceNumber は降順になる。
                new() { LedgerId = 11, SequenceNumber = 3, EntryStation = "博多", ExitStation = "天神", Amount = 260, UseDate = sameDay, Balance = 740 },
                new() { LedgerId = 11, SequenceNumber = 2, EntryStation = "薬院", ExitStation = "大橋", Amount = 210, UseDate = sameDay, Balance = 530 },
                new() { LedgerId = 11, SequenceNumber = 1, EntryStation = "姪浜", ExitStation = "西新", Amount = 230, UseDate = sameDay, Balance = 300 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(11)).ReturnsAsync(ledger);

        List<LedgerDetail>? savedDetails = null;
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(11, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Callback<int, IEnumerable<LedgerDetail>, System.Data.SQLite.SQLiteTransaction>((_, details, _) => savedDetails = details.ToList())
            .ReturnsAsync(true);

        string? savedSummary = null;
        _ledgerRepoMock
            .Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .Callback<int, string, System.Data.SQLite.SQLiteTransaction>((_, summary, _) => savedSummary = summary)
            .ReturnsAsync(true);

        await _viewModel.InitializeAsync(11);

        // Act: 分割線を入れて変更を発生させ、保存する
        _viewModel.SplitAllCommand.Execute(null);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert: DB へは新しい順（＝画面の逆順）で渡る
        savedDetails.Should().NotBeNull();
        savedDetails!.Select(d => d.EntryStation).Should().Equal(
            new[] { "姪浜", "薬院", "博多" },
            "先に INSERT した明細ほど小さい rowid になるため、最新の明細から渡すこと（Issue #1913）");

        // 対の表明: Reverse は DB 呼び出しにだけ適用し、摘要は時系列昇順のまま生成すること。
        // 両方を見ないと「摘要ごと逆順にした」実装でも緑になる。
        savedSummary.Should().NotBeNull();
        savedSummary.Should().Be(
            "鉄道（博多～天神、薬院～大橋、姪浜～西新）",
            "摘要のブロック順は時系列昇順のままであること");
    }

    #endregion
    #region 監査ログの利用明細（Issue #1979）

    /// <summary>
    /// Issue #1979: 明細を編集した保存で、監査ログの変更前・変更後がそれぞれ
    /// 編集<b>前</b>・編集<b>後</b>の明細を記録すること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧実装の「変更前」は 5 フィールドの手組みで <c>Details</c> を持たず、
    /// <c>_ledger.Details</c>（＝「変更後」）は編集後の明細へ差し替えられていなかった。
    /// 明細を表示できるようにした途端、操作ログ画面・Excel に
    /// 「全明細が（なし）から編集<b>前</b>の値へ変わった」という、二重に誤った差分が並ぶ。
    /// </para>
    /// <para>
    /// 「変更前」は下の <c>Items.Select</c> が <c>item.Detail</c>（＝ <c>_ledger.Details</c> と
    /// 同一インスタンス）の <c>GroupId</c> を書き換えるより前に採る必要がある（#1959）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Save_監査ログの変更前と変更後がそれぞれ編集前と編集後の明細を記録すること_Issue1979()
    {
        // Arrange: グループ未設定（自動判定）の 2 明細
        var ledger = new Ledger
        {
            Id = 21,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 2, 10),
            Summary = "鉄道（博多～天神、薬院～大橋）",
            Expense = 470,
            Balance = 530,
            Details = new List<LedgerDetail>
            {
                new() { LedgerId = 21, SequenceNumber = 1, EntryStation = "薬院", ExitStation = "大橋", Amount = 210, UseDate = new DateTime(2026, 2, 10), Balance = 530 },
                new() { LedgerId = 21, SequenceNumber = 2, EntryStation = "博多", ExitStation = "天神", Amount = 260, UseDate = new DateTime(2026, 2, 10), Balance = 740 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(21)).ReturnsAsync(ledger);
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(21, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepoMock
            .Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);

        await _viewModel.InitializeAsync(21);

        // Act: 全明細を 1 グループへまとめて保存する（摘要が変わるので監査ログが記録される）
        _viewModel.MergeAllCommand.Execute(null);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        var log = _operationLogs.Should().ContainSingle(l => l.Action == "UPDATE").Subject;

        var before = ParseDetails(log.BeforeData);
        before.GetArrayLength().Should().Be(2, "編集前の明細が変更前に無いと、全明細が新規追加として描画される");
        before.EnumerateArray().Should().OnlyContain(
            d => d.GetProperty("GroupId").ValueKind == JsonValueKind.Null,
            "編集前はグループ未設定（自動判定）であるべき");

        var after = ParseDetails(log.AfterData);
        after.GetArrayLength().Should().Be(2);
        after.EnumerateArray().Should().OnlyContain(
            d => d.GetProperty("GroupId").GetInt32() == LedgerDetailViewModel.MergedGroupId,
            "変更後は画面で指定した明示グループであるべき（編集前の明細を写していないこと）");
    }

    private static JsonElement ParseDetails(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Details").Clone();
    }

    #endregion
}

