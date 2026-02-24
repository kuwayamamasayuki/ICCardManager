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
public class OperationLogSearchViewModelTests
{
    private readonly Mock<IOperationLogRepository> _repoMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly OperationLogSearchViewModel _viewModel;

    public OperationLogSearchViewModelTests()
    {
        _repoMock = new Mock<IOperationLogRepository>();
        _dialogServiceMock = new Mock<IDialogService>();

        // SearchAsyncのデフォルト: 空結果を返す
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = Array.Empty<OperationLog>(),
                TotalCount = 0,
                CurrentPage = 1,
                PageSize = 50
            });

        _viewModel = new OperationLogSearchViewModel(
            _repoMock.Object,
            _dialogServiceMock.Object,
            new OperationLogExcelExportService());
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
        // Arrange
        _viewModel.FromDate = DateTime.Today.AddMonths(-1);
        _viewModel.ToDate = DateTime.Today.AddMonths(-1);

        // Act
        _viewModel.SetTodayCommand.Execute(null);

        // Assert
        _viewModel.FromDate.Should().Be(DateTime.Today);
        _viewModel.ToDate.Should().Be(DateTime.Today);
    }

    [Fact]
    public void SetThisMonth_今月の全日が設定されること()
    {
        // Act
        _viewModel.SetThisMonthCommand.Execute(null);

        // Assert
        var today = DateTime.Today;
        _viewModel.FromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _viewModel.ToDate.Should().Be(new DateTime(today.Year, today.Month,
            DateTime.DaysInMonth(today.Year, today.Month)));
    }

    [Fact]
    public void SetLastMonth_先月の全日が設定されること()
    {
        // Act
        _viewModel.SetLastMonthCommand.Execute(null);

        // Assert
        var lastMonth = DateTime.Today.AddMonths(-1);
        _viewModel.FromDate.Should().Be(new DateTime(lastMonth.Year, lastMonth.Month, 1));
        _viewModel.ToDate.Should().Be(new DateTime(lastMonth.Year, lastMonth.Month,
            DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)));
    }

    #endregion

    #region フィルタクリア

    [Fact]
    public void ClearFilters_全てのフィルタがリセットされること()
    {
        // Arrange: フィルタを設定
        _viewModel.FromDate = new DateTime(2025, 1, 1);
        _viewModel.ToDate = new DateTime(2025, 1, 31);
        _viewModel.SelectedAction = _viewModel.ActionTypes[1]; // INSERT
        _viewModel.SelectedTargetTable = _viewModel.TargetTables[1]; // staff
        _viewModel.TargetIdFilter = "test-id";
        _viewModel.OperatorNameFilter = "テスト";

        // Act
        _viewModel.ClearFiltersCommand.Execute(null);

        // Assert
        var today = DateTime.Today;
        _viewModel.FromDate.Should().Be(new DateTime(today.Year, today.Month, 1));
        _viewModel.ToDate.Should().Be(today);
        _viewModel.SelectedAction!.Value.Should().BeEmpty();
        _viewModel.SelectedTargetTable!.Value.Should().BeEmpty();
        _viewModel.TargetIdFilter.Should().BeEmpty();
        _viewModel.OperatorNameFilter.Should().BeEmpty();
    }

    #endregion

    #region ページ情報

    [Fact]
    public void PageInfo_データなしの場合に0件と表示されること()
    {
        _viewModel.PageInfo.Should().Be("0件");
    }

    #endregion

    #region 検索・ページネーション

    [Fact]
    public async Task SearchAsync_リポジトリに検索条件を渡すこと()
    {
        // Arrange
        OperationLogSearchCriteria capturedCriteria = null;
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Callback<OperationLogSearchCriteria, int, int>((c, _, __) => capturedCriteria = c)
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = Array.Empty<OperationLog>(),
                TotalCount = 0,
                CurrentPage = 1,
                PageSize = 50
            });

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
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Callback<OperationLogSearchCriteria, int, int>((c, _, __) => capturedCriteria = c)
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = Array.Empty<OperationLog>(),
                TotalCount = 0,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act (デフォルトのまま検索)
        await _viewModel.SearchAsync();

        // Assert
        capturedCriteria!.Action.Should().BeNull();
        capturedCriteria.TargetTable.Should().BeNull();
        capturedCriteria.TargetId.Should().BeNull();
        capturedCriteria.OperatorName.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_複数ページある場合に最終ページに移動すること()
    {
        // Arrange: 1ページ目→3ページある、3ページ目を返す
        var callCount = 0;
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync((OperationLogSearchCriteria _, int page, int pageSize) =>
            {
                callCount++;
                return new OperationLogSearchResult
                {
                    Items = new[]
                    {
                        new OperationLog
                        {
                            Id = page * 100,
                            Timestamp = DateTime.Now,
                            Action = "INSERT",
                            TargetTable = "staff",
                            TargetId = $"id-{page}",
                            OperatorName = "テスト"
                        }
                    },
                    TotalCount = 150,
                    CurrentPage = page,
                    PageSize = pageSize
                };
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 1ページ目取得→最終ページに移動で2回呼ばれる
        callCount.Should().Be(2);
        _viewModel.CurrentPage.Should().Be(3); // 150件÷50件=3ページの最終ページ
        _viewModel.TotalCount.Should().Be(150);
    }

    [Fact]
    public async Task SearchAsync_結果をLogsに正しくマッピングすること()
    {
        // Arrange
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = new DateTime(2025, 6, 15, 10, 30, 0),
            Action = "INSERT",
            TargetTable = "staff",
            TargetId = "ABC123",
            OperatorName = "田中太郎",
            AfterData = "{\"Name\":\"田中太郎\",\"Number\":\"001\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "staff",
            TargetId = "ABC123",
            OperatorName = "管理者",
            AfterData = "{\"Name\":\"田中太郎\",\"Number\":\"001\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 「田中太郎（001）」形式
        _viewModel.Logs[0].TargetDisplayName.Should().Be("田中太郎（001）");
    }

    [Fact]
    public async Task SearchAsync_カードの表示名がJSONから正しく生成されること()
    {
        // Arrange
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "ic_card",
            TargetId = "DEF456",
            OperatorName = "管理者",
            AfterData = "{\"CardType\":\"はやかけん\",\"CardNumber\":\"001\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert: 「はやかけん 001」形式
        _viewModel.Logs[0].TargetDisplayName.Should().Be("はやかけん 001");
    }

    [Fact]
    public async Task SearchAsync_利用履歴の表示名がJSONから正しく生成されること()
    {
        // Arrange
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "ledger",
            TargetId = "42",
            OperatorName = "管理者",
            AfterData = "{\"Date\":\"2025-06-15\",\"Summary\":\"鉄道（博多～天神）\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "DELETE",
            TargetTable = "staff",
            TargetId = "FALLBACK_ID",
            OperatorName = "管理者"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().Be("FALLBACK_ID");
    }

    [Fact]
    public async Task SearchAsync_UPDATE操作の変更内容が生成されること()
    {
        // Arrange
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "UPDATE",
            TargetTable = "staff",
            TargetId = "ABC123",
            OperatorName = "管理者",
            BeforeData = "{\"Name\":\"田中太郎\",\"Number\":\"001\",\"Note\":\"\"}",
            AfterData = "{\"Name\":\"田中花子\",\"Number\":\"001\",\"Note\":\"改姓\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "ic_card",
            TargetId = "XYZ789",
            OperatorName = "管理者"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].DetailSummary.Should().Be("交通系ICカード（XYZ789）を登録");
    }

    [Fact]
    public async Task NextPageAsync_次のページに移動すること()
    {
        // Arrange: 2ページ分のデータ
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync((OperationLogSearchCriteria _, int page, int pageSize) =>
                new OperationLogSearchResult
                {
                    Items = new[]
                    {
                        new OperationLog
                        {
                            Id = page,
                            Timestamp = DateTime.Now,
                            Action = "INSERT",
                            TargetTable = "staff",
                            TargetId = "id",
                            OperatorName = "op"
                        }
                    },
                    TotalCount = 100,
                    CurrentPage = page,
                    PageSize = pageSize
                });

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
        // Arrange: 3ページ分のデータ → 最終ページ(3)からスタート
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync((OperationLogSearchCriteria _, int page, int pageSize) =>
                new OperationLogSearchResult
                {
                    Items = new[]
                    {
                        new OperationLog
                        {
                            Id = page,
                            Timestamp = DateTime.Now,
                            Action = "INSERT",
                            TargetTable = "staff",
                            TargetId = "id",
                            OperatorName = "op"
                        }
                    },
                    TotalCount = 150,
                    CurrentPage = page,
                    PageSize = pageSize
                });

        await _viewModel.SearchAsync();
        _viewModel.CurrentPage.Should().Be(3);

        // Act
        await _viewModel.PreviousPageAsync();

        // Assert
        _viewModel.CurrentPage.Should().Be(2);
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
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[]
                {
                    new OperationLog
                    {
                        Id = 1,
                        Timestamp = DateTime.Now,
                        Action = "INSERT",
                        TargetTable = "staff",
                        TargetId = "id",
                        OperatorName = "op"
                    }
                },
                TotalCount = 5,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.StatusMessage.Should().Be("5件の操作ログが見つかりました");
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
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[]
                {
                    new OperationLog
                    {
                        Id = 1,
                        Timestamp = DateTime.Now,
                        Action = action,
                        TargetTable = "staff",
                        TargetId = "id",
                        OperatorName = "op"
                    }
                },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[]
                {
                    new OperationLog
                    {
                        Id = 1,
                        Timestamp = DateTime.Now,
                        Action = "INSERT",
                        TargetTable = table,
                        TargetId = "id",
                        OperatorName = "op"
                    }
                },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "UPDATE",
            TargetTable = "staff",
            TargetId = "ABC",
            OperatorName = "管理者",
            BeforeData = "{\"Name\":\"田中太郎\",\"Number\":\"001\",\"Note\":\"\"}",
            AfterData = "{\"Name\":\"田中太郎\",\"Number\":\"001\",\"Note\":\"メモ追加\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "UPDATE",
            TargetTable = "ic_card",
            TargetId = "XYZ",
            OperatorName = "管理者",
            BeforeData = "{\"CardType\":\"はやかけん\",\"CardNumber\":\"\",\"Note\":\"\"}",
            AfterData = "{\"CardType\":\"はやかけん\",\"CardNumber\":\"001\",\"Note\":\"\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "UPDATE",
            TargetTable = "staff",
            TargetId = "ABC",
            OperatorName = "管理者",
            BeforeData = "not-json",
            AfterData = "not-json"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "ledger",
            TargetId = "42",
            OperatorName = "管理者",
            AfterData = $"{{\"Date\":\"2025-06-15\",\"Summary\":\"{longSummary}\"}}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

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
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "staff",
            TargetId = "ABC123",
            OperatorName = "管理者",
            AfterData = "{\"StaffIdm\":\"0123456789ABCDEF\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().Be("0123456789ABCDEF");
    }

    [Fact]
    public async Task 名前のみで番号なしの場合に名前のみが表示されること()
    {
        // Arrange
        var testLog = new OperationLog
        {
            Id = 1,
            Timestamp = DateTime.Now,
            Action = "INSERT",
            TargetTable = "staff",
            TargetId = "ABC123",
            OperatorName = "管理者",
            AfterData = "{\"Name\":\"田中太郎\"}"
        };

        _repoMock.Setup(r => r.SearchAsync(
                It.IsAny<OperationLogSearchCriteria>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new OperationLogSearchResult
            {
                Items = new[] { testLog },
                TotalCount = 1,
                CurrentPage = 1,
                PageSize = 50
            });

        // Act
        await _viewModel.SearchAsync();

        // Assert
        _viewModel.Logs[0].TargetDisplayName.Should().Be("田中太郎");
    }

    #endregion
}
