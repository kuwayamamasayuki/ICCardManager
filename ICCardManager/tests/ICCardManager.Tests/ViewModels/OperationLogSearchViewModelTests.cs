using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// OperationLogSearchViewModelの単体テスト
/// </summary>
/// <remarks>
/// Issue #1479 で OFFSET pagination から keyset pagination に切り替えたため、
/// すべてのモックは <see cref="IOperationLogRepository.SearchFirstPageAsync"/> など
/// 4 種類の keyset メソッドを対象としている。
/// </remarks>
public class OperationLogSearchViewModelTests
{
    private readonly Mock<IOperationLogRepository> _repoMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<OperationLogExcelExportService> _excelExportServiceMock;
    private readonly Mock<ICCardManager.Services.ISafeFileLauncher> _safeFileLauncherMock;
    private readonly OperationLogSearchViewModel _viewModel;

    public OperationLogSearchViewModelTests()
    {
        _repoMock = new Mock<IOperationLogRepository>();
        _dialogServiceMock = new Mock<IDialogService>();
        _excelExportServiceMock = new Mock<OperationLogExcelExportService>();

        // デフォルト: 空ページを返す（4 keyset メソッドすべて）
        SetupKeysetReturning(Array.Empty<OperationLog>());

        _safeFileLauncherMock = new Mock<ICCardManager.Services.ISafeFileLauncher>();
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Ok());

        _viewModel = new OperationLogSearchViewModel(
            _repoMock.Object,
            _dialogServiceMock.Object,
            _excelExportServiceMock.Object,
            _safeFileLauncherMock.Object);
    }

    /// <summary>
    /// 4 種類の keyset メソッドすべてに同じページを返すモックを仕込む。
    /// 多くのテストはページ遷移の経路ではなく「結果の表示変換」を検証するため、
    /// どのメソッドが呼ばれても同じデータを返せば十分。
    /// </summary>
    private void SetupKeysetReturning(
        IReadOnlyList<OperationLog> items,
        int? totalCount = null,
        bool hasPrevious = false,
        bool hasNext = false)
    {
        var page = BuildPage(items, totalCount, hasPrevious, hasNext);
        _repoMock.Setup(r => r.SearchFirstPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(page);
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(page);
        _repoMock.Setup(r => r.SearchNextPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<OperationLogCursor>(), It.IsAny<int>()))
            .ReturnsAsync(page);
        _repoMock.Setup(r => r.SearchPreviousPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<OperationLogCursor>(), It.IsAny<int>()))
            .ReturnsAsync(page);
    }

    private static OperationLogKeysetPage BuildPage(
        IReadOnlyList<OperationLog> items,
        int? totalCount,
        bool hasPrevious,
        bool hasNext)
    {
        return new OperationLogKeysetPage
        {
            Items = items,
            TotalCount = totalCount ?? items.Count,
            FirstCursor = items.Count > 0 ? new OperationLogCursor(items[0].Timestamp, items[0].Id) : null,
            LastCursor = items.Count > 0 ? new OperationLogCursor(items[items.Count - 1].Timestamp, items[items.Count - 1].Id) : null,
            HasPrevious = hasPrevious,
            HasNext = hasNext
        };
    }

    private static OperationLog MakeLog(int id, string action = "INSERT", string targetTable = "staff", string targetId = "id", string operatorName = "op", DateTime? timestamp = null, string beforeData = null, string afterData = null)
    {
        return new OperationLog
        {
            Id = id,
            Timestamp = timestamp ?? new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(id),
            Action = action,
            TargetTable = targetTable,
            TargetId = targetId,
            OperatorName = operatorName,
            BeforeData = beforeData,
            AfterData = afterData
        };
    }

    #region コンストラクタ・初期状態

    [Fact]
    public void Constructor_デフォルトで今月の期間が設定されること()
    {
        var today = DateTime.Today;
        _viewModel.FromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _viewModel.ToDate.Should().Be(today);
    }

    [Fact]
    public void Constructor_デフォルトで全ての操作種別が選択されていること()
    {
        _viewModel.SelectedAction.Should().NotBeNull();
        _viewModel.SelectedAction!.Value.Should().BeEmpty();
        _viewModel.SelectedAction.DisplayName.Should().Be("すべて");
    }

    [Fact]
    public void Constructor_デフォルトで全ての対象テーブルが選択されていること()
    {
        _viewModel.SelectedTargetTable.Should().NotBeNull();
        _viewModel.SelectedTargetTable!.Value.Should().BeEmpty();
        _viewModel.SelectedTargetTable.DisplayName.Should().Be("すべて");
    }

    [Fact]
    public void Constructor_操作種別の選択肢が正しいこと()
    {
        _viewModel.ActionTypes.Should().HaveCount(4);
        _viewModel.ActionTypes.Select(a => a.Value)
            .Should().BeEquivalentTo(new[] { "", "INSERT", "UPDATE", "DELETE" });
    }

    [Fact]
    public void Constructor_対象テーブルの選択肢が正しいこと()
    {
        _viewModel.TargetTables.Should().HaveCount(4);
        _viewModel.TargetTables.Select(t => t.Value)
            .Should().BeEquivalentTo(new[] { "", "staff", "ic_card", "ledger" });
    }

    #endregion

    #region 日付プリセットコマンド

    [Fact]
    public void SetToday_今日の日付が設定されること()
    {
        var today = DateTime.Today;
        _viewModel.SetTodayCommand.Execute(null);
        _viewModel.FromDate.Should().Be(today);
        _viewModel.ToDate.Should().Be(today);
    }

    [Fact]
    public void SetThisMonth_今月の日付が設定されること()
    {
        _viewModel.SetThisMonthCommand.Execute(null);
        var today = DateTime.Today;
        _viewModel.FromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _viewModel.ToDate.Should().Be(new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)));
    }

    [Fact]
    public void SetLastMonth_先月の日付が設定されること()
    {
        _viewModel.SetLastMonthCommand.Execute(null);
        var lastMonth = DateTime.Today.AddMonths(-1);
        _viewModel.FromDate.Should().Be(new DateTime(lastMonth.Year, lastMonth.Month, 1));
        _viewModel.ToDate.Should().Be(new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)));
    }

    [Fact]
    public void ClearFilters_フィルタが初期化されること()
    {
        _viewModel.SelectedAction = _viewModel.ActionTypes[1];
        _viewModel.SelectedTargetTable = _viewModel.TargetTables[1];
        _viewModel.TargetIdFilter = "ABC";
        _viewModel.OperatorNameFilter = "山田";

        _viewModel.ClearFiltersCommand.Execute(null);

        _viewModel.SelectedAction.Should().Be(_viewModel.ActionTypes[0]);
        _viewModel.SelectedTargetTable.Should().Be(_viewModel.TargetTables[0]);
        _viewModel.TargetIdFilter.Should().BeEmpty();
        _viewModel.OperatorNameFilter.Should().BeEmpty();
    }

    #endregion

    #region 検索・ページネーション

    [Fact]
    public async Task SearchAsync_リポジトリに検索条件を渡すこと()
    {
        // Arrange
        OperationLogSearchCriteria capturedCriteria = null;
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .Callback<OperationLogSearchCriteria, int>((c, _) => capturedCriteria = c)
            .ReturnsAsync(BuildPage(Array.Empty<OperationLog>(), 0, false, false));

        _viewModel.SelectedAction = _viewModel.ActionTypes[1]; // INSERT
        _viewModel.SelectedTargetTable = _viewModel.TargetTables[2]; // ic_card
        _viewModel.TargetIdFilter = " ABC123 ";
        _viewModel.OperatorNameFilter = " 田中 ";

        // Act
        await _viewModel.SearchAsync();

        // Assert
        capturedCriteria.Should().NotBeNull();
        capturedCriteria!.Action.Should().Be("INSERT");
        capturedCriteria.TargetTable.Should().Be("ic_card");
        capturedCriteria.TargetId.Should().Be("ABC123"); // トリミング
        capturedCriteria.OperatorName.Should().Be("田中"); // トリミング
    }

    [Fact]
    public async Task SearchAsync_全件選択時にnullが渡されること()
    {
        // Arrange
        OperationLogSearchCriteria capturedCriteria = null;
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .Callback<OperationLogSearchCriteria, int>((c, _) => capturedCriteria = c)
            .ReturnsAsync(BuildPage(Array.Empty<OperationLog>(), 0, false, false));

        // Act (デフォルトのまま検索)
        await _viewModel.SearchAsync();

        // Assert
        capturedCriteria!.Action.Should().BeNull();
        capturedCriteria.TargetTable.Should().BeNull();
        capturedCriteria.TargetId.Should().BeNull();
        capturedCriteria.OperatorName.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_直接最終ページを取得する_Issue1479()
    {
        // Arrange: SearchLastPageAsync が 1 度だけ呼ばれること（Issue #1479: keyset により First→Last の往復が不要）
        var lastPageCallCount = 0;
        var firstPageCallCount = 0;
        var lastPageItems = new[] { MakeLog(150) };
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .Callback(() => lastPageCallCount++)
            .ReturnsAsync(BuildPage(lastPageItems, totalCount: 150, hasPrevious: true, hasNext: false));
        _repoMock.Setup(r => r.SearchFirstPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .Callback(() => firstPageCallCount++)
            .ReturnsAsync(BuildPage(Array.Empty<OperationLog>(), 0, false, false));

        // Act
        await _viewModel.SearchAsync();

        // Assert
        lastPageCallCount.Should().Be(1);
        firstPageCallCount.Should().Be(0);  // OFFSET 時代のような「先頭ページ経由」は不要
        _viewModel.TotalCount.Should().Be(150);
        _viewModel.CurrentPage.Should().Be(3);  // 150件÷50件=3ページ
    }

    [Fact]
    public async Task SearchAsync_結果をLogsに正しくマッピングすること()
    {
        // Arrange
        var testLog = MakeLog(1, action: "INSERT", targetTable: "staff", targetId: "ABC123",
            operatorName: "田中太郎",
            afterData: "{\"Name\":\"田中太郎\",\"Number\":\"001\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs.Should().HaveCount(1);
        var item = _viewModel.Logs[0];
        item.Id.Should().Be(1);
        item.Action.Should().Be("INSERT");
        item.ActionDisplay.Should().Be("登録");
        item.TargetTable.Should().Be("staff");
        item.TargetTableDisplay.Should().Be("職員");
        item.OperatorName.Should().Be("田中太郎");
    }

    [Fact]
    public async Task SearchAsync_職員の表示名がJSONから正しく生成されること()
    {
        // Arrange
        var testLog = MakeLog(1, targetTable: "staff", targetId: "ABC123", operatorName: "管理者",
            afterData: "{\"Name\":\"田中太郎\",\"Number\":\"001\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 「田中太郎（001）」形式
        _viewModel.Logs[0].TargetDisplayName.Should().Be("田中太郎（001）");
    }

    [Fact]
    public async Task SearchAsync_カードの表示名がJSONから正しく生成されること()
    {
        // Arrange
        var testLog = MakeLog(1, targetTable: "ic_card", targetId: "DEF456", operatorName: "管理者",
            afterData: "{\"CardType\":\"はやかけん\",\"CardNumber\":\"001\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 「はやかけん 001」形式
        _viewModel.Logs[0].TargetDisplayName.Should().Be("はやかけん 001");
    }

    [Fact]
    public async Task SearchAsync_利用履歴の表示名がJSONから正しく生成されること()
    {
        // Arrange
        var testLog = MakeLog(1, targetTable: "ledger", targetId: "42", operatorName: "管理者",
            afterData: "{\"Date\":\"2025-06-15\",\"Summary\":\"鉄道（博多～天神）\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 「R7.6.15 鉄道（博多～天神）」形式
        var displayName = _viewModel.Logs[0].TargetDisplayName;
        displayName.Should().Contain("鉄道（博多～天神）");
        displayName.Should().Contain("R7"); // 令和7年
    }

    [Fact]
    public async Task SearchAsync_JSONが無い場合にTargetIdが表示名になること()
    {
        // Arrange
        var testLog = MakeLog(1, action: "DELETE", targetTable: "staff", targetId: "FALLBACK_ID", operatorName: "管理者");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().Be("FALLBACK_ID");
    }

    [Fact]
    public async Task SearchAsync_UPDATE操作の変更内容が生成されること()
    {
        // Arrange
        var testLog = MakeLog(1, action: "UPDATE", targetTable: "staff", targetId: "ABC123", operatorName: "管理者",
            beforeData: "{\"Name\":\"田中太郎\",\"Number\":\"001\",\"Note\":\"\"}",
            afterData: "{\"Name\":\"田中花子\",\"Number\":\"001\",\"Note\":\"改姓\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        var summary = _viewModel.Logs[0].DetailSummary;
        summary.Should().Contain("職員");
        summary.Should().Contain("更新");
        summary.Should().Contain("氏名");
        summary.Should().Contain("田中太郎→田中花子");
    }

    [Fact]
    public async Task SearchAsync_INSERT操作の詳細サマリーが正しいこと()
    {
        // Arrange
        var testLog = MakeLog(1, action: "INSERT", targetTable: "ic_card", targetId: "XYZ789", operatorName: "管理者");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].DetailSummary.Should().Be("交通系ICカード（XYZ789）を登録");
    }

    [Fact]
    public async Task NextPageAsync_次のページに移動すること()
    {
        // Arrange: SearchLastPageAsync で page2 を返す（hasPrevious=true）
        // FirstPageAsync で page1 を返す（hasNext=true, hasPrevious=false）
        // NextPageAsync で page2 を返す（hasNext=false, hasPrevious=true）
        var firstPageItems = new[] { MakeLog(1) };
        var lastPageItems = new[] { MakeLog(2) };

        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(lastPageItems, totalCount: 100, hasPrevious: true, hasNext: false));
        _repoMock.Setup(r => r.SearchFirstPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(firstPageItems, totalCount: 100, hasPrevious: false, hasNext: true));
        _repoMock.Setup(r => r.SearchNextPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<OperationLogCursor>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(lastPageItems, totalCount: 100, hasPrevious: true, hasNext: false));

        await _viewModel.SearchAsync(); // 最終ページ(2)に移動
        _viewModel.CurrentPage.Should().Be(2);

        // 1ページ目に戻す
        await _viewModel.FirstPageAsync();
        _viewModel.CurrentPage.Should().Be(1);

        // Act: 次のページへ
        await _viewModel.NextPageAsync();

        // Assert
        _viewModel.CurrentPage.Should().Be(2);
    }

    [Fact]
    public async Task PreviousPageAsync_前のページに移動すること()
    {
        // Arrange: 3ページ分のデータ → 最終ページ(3)からスタート → PreviousPage → page2
        var lastPageItems = new[] { MakeLog(150) };
        var prevPageItems = new[] { MakeLog(100) };

        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(lastPageItems, totalCount: 150, hasPrevious: true, hasNext: false));
        _repoMock.Setup(r => r.SearchPreviousPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<OperationLogCursor>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(prevPageItems, totalCount: 150, hasPrevious: true, hasNext: true));

        await _viewModel.SearchAsync();
        _viewModel.CurrentPage.Should().Be(3);

        // Act
        await _viewModel.PreviousPageAsync();

        // Assert
        _viewModel.CurrentPage.Should().Be(2);
    }

    [Fact]
    public async Task LastPageAsync_最終ページに移動すること()
    {
        // Arrange
        var lastPageItems = new[] { MakeLog(99) };
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(lastPageItems, totalCount: 100, hasPrevious: true, hasNext: false));

        // 一旦 first へ
        var firstPageItems = new[] { MakeLog(1) };
        _repoMock.Setup(r => r.SearchFirstPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(firstPageItems, totalCount: 100, hasPrevious: false, hasNext: true));

        await _viewModel.SearchAsync();   // 最終(2)
        await _viewModel.FirstPageAsync(); // 1
        _viewModel.CurrentPage.Should().Be(1);

        // Act
        await _viewModel.LastPageAsync();

        // Assert
        _viewModel.CurrentPage.Should().Be(2);
    }

    [Fact]
    public async Task PreviousPageAsync_HasPreviousがfalseなら呼ばれないこと()
    {
        // Arrange: 1 ページしか無い結果
        var items = new[] { MakeLog(1) };
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(items, totalCount: 1, hasPrevious: false, hasNext: false));

        await _viewModel.SearchAsync();
        _viewModel.HasPreviousPage.Should().BeFalse();

        // Act
        await _viewModel.PreviousPageAsync();

        // Assert: SearchPreviousPageAsync が呼ばれていない
        _repoMock.Verify(r => r.SearchPreviousPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<OperationLogCursor>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task NextPageAsync_HasNextがfalseなら呼ばれないこと()
    {
        // Arrange
        var items = new[] { MakeLog(1) };
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .ReturnsAsync(BuildPage(items, totalCount: 1, hasPrevious: false, hasNext: false));

        await _viewModel.SearchAsync();
        _viewModel.HasNextPage.Should().BeFalse();

        // Act
        await _viewModel.NextPageAsync();

        // Assert
        _repoMock.Verify(r => r.SearchNextPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<OperationLogCursor>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_結果0件の場合のステータスメッセージ()
    {
        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("条件に一致する操作ログはありません");
        _viewModel.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_結果がある場合のステータスメッセージ()
    {
        // Arrange
        SetupKeysetReturning(new[] { MakeLog(1) }, totalCount: 5);

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("5件の操作ログが見つかりました");
    }

    [Fact]
    public async Task OnPageSizeChanged_ページサイズ変更で再検索されること()
    {
        // Arrange
        await _viewModel.SearchAsync(); // 初期化（空ページ）

        var capturedPageSize = 0;
        _repoMock.Setup(r => r.SearchLastPageAsync(It.IsAny<OperationLogSearchCriteria>(), It.IsAny<int>()))
            .Callback<OperationLogSearchCriteria, int>((_, size) => capturedPageSize = size)
            .ReturnsAsync(BuildPage(Array.Empty<OperationLog>(), 0, false, false));

        // Act
        _viewModel.PageSize = 100;
        // OnPageSizeChanged は fire-and-forget なので少し待つ
        await Task.Delay(50);

        // Assert: 新しい PageSize で呼ばれた
        capturedPageSize.Should().Be(100);
    }

    #endregion

    #region OperationLogDisplayItem の表示変換

    [Theory]
    [InlineData("INSERT", "登録")]
    [InlineData("UPDATE", "更新")]
    [InlineData("DELETE", "削除")]
    [InlineData("UNKNOWN", "UNKNOWN")]
    public async Task ActionDisplay_操作種別が正しく日本語変換されること(string action, string expected)
    {
        // Arrange
        SetupKeysetReturning(new[] { MakeLog(1, action: action) });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].ActionDisplay.Should().Be(expected);
    }

    [Theory]
    [InlineData("staff", "職員")]
    [InlineData("ic_card", "交通系ICカード")]
    [InlineData("ledger", "利用履歴")]
    [InlineData("other", "other")]
    public async Task TargetTableDisplay_対象テーブルが正しく日本語変換されること(string table, string expected)
    {
        // Arrange
        SetupKeysetReturning(new[] { MakeLog(1, targetTable: table) });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetTableDisplay.Should().Be(expected);
    }

    #endregion

    #region 変更内容詳細（GetChangedFieldsDescription間接テスト）

    [Fact]
    public async Task UPDATE時_変更が無いフィールドは表示されないこと()
    {
        // Arrange
        var testLog = MakeLog(1, action: "UPDATE", targetTable: "staff", targetId: "ABC", operatorName: "管理者",
            beforeData: "{\"Name\":\"田中太郎\",\"Number\":\"001\",\"Note\":\"\"}",
            afterData: "{\"Name\":\"田中太郎\",\"Number\":\"001\",\"Note\":\"メモ追加\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 氏名と職員番号は変わっていないので表示されない
        var summary = _viewModel.Logs[0].DetailSummary;
        summary.Should().NotContain("氏名");
        summary.Should().NotContain("職員番号");
        summary.Should().Contain("備考");
    }

    [Fact]
    public async Task UPDATE時_空からの値変更がなしとして表示されること()
    {
        // Arrange
        var testLog = MakeLog(1, action: "UPDATE", targetTable: "ic_card", targetId: "XYZ", operatorName: "管理者",
            beforeData: "{\"CardType\":\"はやかけん\",\"CardNumber\":\"\",\"Note\":\"\"}",
            afterData: "{\"CardType\":\"はやかけん\",\"CardNumber\":\"001\",\"Note\":\"\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        var summary = _viewModel.Logs[0].DetailSummary;
        summary.Should().Contain("（なし）→001");
    }

    [Fact]
    public async Task 不正なJSON_DetailSummaryがフォールバックすること()
    {
        // Arrange
        var testLog = MakeLog(1, action: "UPDATE", targetTable: "staff", targetId: "ABC", operatorName: "管理者",
            beforeData: "not-json",
            afterData: "not-json");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert: JSON解析失敗時はIDを含むフォールバック
        _viewModel.Logs[0].DetailSummary.Should().Contain("ABC");
    }

    [Fact]
    public async Task 長い摘要が25文字で省略されること()
    {
        // Arrange
        var longSummary = "鉄道（博多～天神、天神～薬院、薬院～大橋、大橋～春日原）"; // 25文字超
        var testLog = MakeLog(1, action: "INSERT", targetTable: "ledger", targetId: "42", operatorName: "管理者",
            afterData: $"{{\"Date\":\"2025-06-15\",\"Summary\":\"{longSummary}\"}}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().EndWith("...");
    }

    #endregion

    #region 職員表示名のエッジケース

    [Fact]
    public async Task 職員証IDmしかない場合にIDmが表示名になること()
    {
        // Arrange
        var testLog = MakeLog(1, targetTable: "staff", targetId: "ABC123", operatorName: "管理者",
            afterData: "{\"StaffIdm\":\"0123456789ABCDEF\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().Be("0123456789ABCDEF");
    }

    [Fact]
    public async Task 名前のみで番号なしの場合に名前のみが表示されること()
    {
        // Arrange
        var testLog = MakeLog(1, targetTable: "staff", targetId: "ABC123", operatorName: "管理者",
            afterData: "{\"Name\":\"田中太郎\"}");
        SetupKeysetReturning(new[] { testLog });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().Be("田中太郎");
    }

    #endregion

    #region Issue #1383: エクスポート完了時にプログレスバー(IsBusy)がダイアログ表示前に閉じること

    /// <summary>
    /// 成功時、ShowInformationが呼ばれる時点でIsBusy=falseになっていること。
    /// BeginBusyスコープ内でMessageBoxを表示するとモーダル中プログレスバーが残るため、
    /// スコープを抜けてから表示する修正が効いていることを確認する。
    /// </summary>
    [Fact]
    public async Task ExportToExcelFileAsync_成功時_ShowInformation呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"op_{Guid.NewGuid()}.xlsx");
        _repoMock
            .Setup(r => r.SearchAllAsync(It.IsAny<OperationLogSearchCriteria>()))
            .ReturnsAsync(Array.Empty<OperationLog>());
        _excelExportServiceMock
            .Setup(s => s.ExportAsync(It.IsAny<IEnumerable<OperationLog>>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        bool? isBusyAtShowInformation = null;
        _dialogServiceMock
            .Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtShowInformation = _viewModel.IsBusy);

        // Act
        await _viewModel.ExportToExcelFileAsync(tempPath);

        // Assert
        isBusyAtShowInformation.Should().NotBeNull("成功メッセージダイアログが表示されているはず");
        isBusyAtShowInformation.Should().BeFalse("Issue #1383: ダイアログ表示時にはプログレスバーが閉じていること");
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// 失敗時、ShowErrorが呼ばれる時点でIsBusy=falseになっていること。
    /// </summary>
    [Fact]
    public async Task ExportToExcelFileAsync_失敗時_ShowError呼び出し時点でIsBusyがfalseであること()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"op_{Guid.NewGuid()}.xlsx");
        _repoMock
            .Setup(r => r.SearchAllAsync(It.IsAny<OperationLogSearchCriteria>()))
            .ThrowsAsync(new InvalidOperationException("テスト用例外"));

        bool? isBusyAtShowError = null;
        _dialogServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => isBusyAtShowError = _viewModel.IsBusy);

        // Act
        await _viewModel.ExportToExcelFileAsync(tempPath);

        // Assert
        isBusyAtShowError.Should().NotBeNull("エラーダイアログが表示されているはず");
        isBusyAtShowError.Should().BeFalse("Issue #1383: エラーダイアログ表示時にもプログレスバーが閉じていること");
        _viewModel.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// エクスポート失敗時、生の <c>ex.Message</c> を ShowError／StatusMessage に漏らさず、
    /// 3要素準拠（操作名を含み「～ください。」で終わる）の文言を表示すること（Issue #1614）。
    /// </summary>
    [Fact]
    public async Task ExportToExcelFileAsync_失敗時_生の例外メッセージを漏らさず3要素文言を表示すること()
    {
        // Arrange
        const string rawTechnicalDetail = "Object reference not set to an instance of an object.";
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"op_{Guid.NewGuid()}.xlsx");
        _repoMock
            .Setup(r => r.SearchAllAsync(It.IsAny<OperationLogSearchCriteria>()))
            .ThrowsAsync(new InvalidOperationException(rawTechnicalDetail));

        string? shownMessage = null;
        _dialogServiceMock
            .Setup(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => shownMessage = message);

        // Act
        await _viewModel.ExportToExcelFileAsync(tempPath);

        // Assert - ダイアログ
        shownMessage.Should().NotBeNull();
        shownMessage.Should().NotContain(rawTechnicalDetail);   // 生の技術詳細が漏れない
        shownMessage.Should().Contain("操作ログのエクスポート");  // 「何が」= 操作名
        shownMessage.Should().EndWith("ください。");             // 行動指示で終わる

        // Assert - ステータスバー
        _viewModel.StatusMessage.Should().NotContain(rawTechnicalDetail);
        _viewModel.StatusMessage.Should().Contain("操作ログのエクスポート");
    }

    #endregion

    #region Issue #1548/#1507: PageNumberDisplay / PageInfo 依存通知

    // 派生プロパティ PageNumberDisplay / PageInfo は CurrentPage / TotalPages / TotalCount / PageSize の
    // setter から [NotifyPropertyChangedFor] により自動通知されることを検証する。
    // この通知が無いと、OperationLogDialog の OnViewModelPropertyChanged ハンドラが LiveRegionChanged を
    // 発火するチャンスを失い、スクリーンリーダー（Narrator/NVDA）でページ送り完了が読み上げられない。

    [Fact]
    public void CurrentPage変更で_PageInfoとPageNumberDisplayの両方の通知が発火すること()
    {
        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        _viewModel.CurrentPage = 3;

        notified.Should().Contain(nameof(OperationLogSearchViewModel.PageInfo));
        notified.Should().Contain(nameof(OperationLogSearchViewModel.PageNumberDisplay));
    }

    [Fact]
    public void TotalPages変更で_PageNumberDisplayの通知が発火すること()
    {
        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        _viewModel.TotalPages = 5;

        notified.Should().Contain(nameof(OperationLogSearchViewModel.PageNumberDisplay));
    }

    [Fact]
    public void TotalCount変更で_PageInfoの通知が発火すること()
    {
        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        _viewModel.TotalCount = 123;

        notified.Should().Contain(nameof(OperationLogSearchViewModel.PageInfo));
    }

    [Fact]
    public void PageSize変更で_PageInfoの通知が発火すること()
    {
        var notified = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        _viewModel.PageSize = 100;

        notified.Should().Contain(nameof(OperationLogSearchViewModel.PageInfo));
    }

    [Fact]
    public void PageNumberDisplayは_CurrentPageとTotalPagesを連結した文字列であること()
    {
        _viewModel.CurrentPage = 2;
        _viewModel.TotalPages = 7;

        _viewModel.PageNumberDisplay.Should().Be("2 / 7 ページ");
    }

    [Fact]
    public void PageNumberDisplayは_読み上げ用に末尾に_ページ_を含むこと()
    {
        // Issue #1548/#1507: Narrator が「N / M ページ」と読み上げるよう、末尾の "ページ" を派生プロパティに含める
        // （元の XAML では <Run Text=" ページ"/> として静的 Run で含まれていた）。
        _viewModel.CurrentPage = 1;
        _viewModel.TotalPages = 1;

        _viewModel.PageNumberDisplay.Should().EndWith(" ページ");
    }

    [Theory]
    [InlineData(1, 1, 5, "ページ 1 / 1 に移動しました（合計 5 件）")]
    [InlineData(2, 3, 42, "ページ 2 / 3 に移動しました（合計 42 件）")]
    [InlineData(10, 10, 200, "ページ 10 / 10 に移動しました（合計 200 件）")]
    public void FormatPageNavigationStatus_想定形式の文字列を返すこと(
        int currentPage, int totalPages, int totalCount, string expected)
    {
        // Issue #1507: ページ送り完了時の StatusMessage は「ページ N / M に移動しました（合計 X 件）」形式。
        // この文字列は検索時の "N 件の操作ログが見つかりました" と意図的に異なる表現にしてあり、
        // PropertyChanged が確実に発火（値変化）して Narrator が Polite Live Region として読み上げる。
        // フォーマット変更でアナウンス機能が壊れないよう、純粋関数として固定する。
        var result = OperationLogSearchViewModel.FormatPageNavigationStatus(currentPage, totalPages, totalCount);

        result.Should().Be(expected);
    }

    #endregion

    #region OpenExportedFile（Issue #1465）

    [Fact]
    public void OpenExportedFile_ISafeFileLauncherへ委譲()
    {
        _viewModel.LastExportedFile = "C:\\export.xlsx";

        _viewModel.OpenExportedFileCommand.Execute(null);

        _safeFileLauncherMock.Verify(l => l.LaunchFile("C:\\export.xlsx"), Times.Once);
    }

    [Fact]
    public void OpenExportedFile_launcher失敗時_エラー表示()
    {
        _viewModel.LastExportedFile = "C:\\evil.exe";
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(ICCardManager.Services.SafeFileLaunchResult.Fail("拡張子NG"));

        _viewModel.OpenExportedFileCommand.Execute(null);

        _viewModel.StatusMessage.Should().Contain("拡張子NG");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    #endregion
}
