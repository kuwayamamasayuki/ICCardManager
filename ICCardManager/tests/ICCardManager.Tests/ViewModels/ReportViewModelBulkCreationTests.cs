using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 一括帳票作成ループの単体テスト（Issue #1949）
/// </summary>
/// <remarks>
/// <para>
/// <c>ReportViewModel.CreateReportAsync</c> のループは <c>await</c> をまたいで
/// <c>SelectedCards</c> を引き直していたため、処理中に選択が変わると
/// <see cref="InvalidOperationException"/>（<c>First()</c> が一致なし）で一括作成全体が
/// 未処理例外になり、件数表示もずれていた。
/// </para>
/// <para>
/// 処理中オーバーレイが塞ぐのはマウスのヒットテストだけ（Issue #1761）なので、
/// キーボード操作でチェックを外す経路が実在する。
/// </para>
/// <para>
/// ループ本体は実ファイル生成（テンプレート解決・Excel 出力）を伴うため、
/// <c>ReportService.CreateMonthlyReportAsync</c> を継ぎ目（virtual）として差し替える。
/// </para>
/// </remarks>
public class ReportViewModelBulkCreationTests : IDisposable
{
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<INavigationService> _navigationServiceMock = new();
    private readonly Mock<ISafeFileLauncher> _safeFileLauncherMock = new();
    private readonly Mock<IReportDataBuilder> _preflightDataBuilderMock = new();
    private readonly Mock<IReportExportStatusService> _exportStatusServiceMock = new();
    private readonly Mock<ReportService> _reportServiceMock;
    private readonly Mock<PrintService> _printServiceMock;
    private readonly ReportViewModel _viewModel;
    private readonly string _outputFolder;

    /// <summary>実際に帳票作成が要求されたカードIDm（要求順）</summary>
    private readonly List<string> _createdForCardIdms = new();

    /// <summary>実際に帳票作成が要求された対象年月と出力先（要求順。Issue #1949）</summary>
    private readonly List<(int Year, int Month, string OutputPath)> _createdRequests = new();

    /// <summary>プレビュー用データが要求されたカードIDmと対象年月（要求順。Issue #1949）</summary>
    private readonly List<(string CardIdm, int Year, int Month)> _previewRequests = new();

    public ReportViewModelBulkCreationTests()
    {
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        var reportDataBuilder = new ReportDataBuilder(
            _cardRepositoryMock.Object, _ledgerRepositoryMock.Object);

        // Issue #1949: CreateMonthlyReportAsync だけを差し替え、ファイル名生成などは実装をそのまま使う
        _reportServiceMock = new Mock<ReportService>(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            reportDataBuilder,
            (IOptions<OrganizationOptions>)null,
            (IReportFileNameFactory)null)
        {
            CallBase = true
        };
        SetupReportCreation(_ => ReportGenerationResult.SuccessResult("dummy"));

        // プリフライトは警告なし（帳票データを構築できない状態）
        _preflightDataBuilderMock
            .Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((MonthlyReportData)null);
        _ledgerRepositoryMock.Setup(r => r.GetAllLentRecordsAsync()).ReturnsAsync(new List<Ledger>());

        _exportStatusServiceMock
            .Setup(s => s.GetStatuses(
                It.IsAny<IEnumerable<ReportExportTarget>>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(new List<ReportExportStatus>());

        // Issue #1949: 複数カードのプレビュー生成ループが全カードを同じ年月で取得することを表明する
        _printServiceMock = new Mock<PrintService>(reportDataBuilder, (IOptions<OrganizationOptions>)null) { CallBase = true };
        SetupPreviewData();

        _safeFileLauncherMock.Setup(l => l.LaunchFolder(It.IsAny<string>()))
            .Returns(SafeFileLaunchResult.Ok());
        _safeFileLauncherMock.Setup(l => l.LaunchFile(It.IsAny<string>()))
            .Returns(SafeFileLaunchResult.Ok());

        _viewModel = new ReportViewModel(
            _reportServiceMock.Object,
            _printServiceMock.Object,
            _cardRepositoryMock.Object,
            _navigationServiceMock.Object,
            _settingsRepositoryMock.Object,
            _safeFileLauncherMock.Object,
            new ReportPreflightChecker(_preflightDataBuilderMock.Object, _ledgerRepositoryMock.Object),
            _exportStatusServiceMock.Object);

        // 既存ファイルがないフォルダを使う（上書き確認ダイアログを挟まないため）
        _outputFolder = Path.Combine(Path.GetTempPath(), "ICCardManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputFolder);
        _viewModel.OutputFolder = _outputFolder;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_outputFolder))
            {
                Directory.Delete(_outputFolder, recursive: true);
            }
        }
        catch (IOException)
        {
            // テスト後始末の失敗はテスト結果に影響させない
        }
    }

    /// <summary>
    /// 帳票生成の結果を、対象カードIDmから決める形で設定する
    /// </summary>
    private void SetupReportCreation(Func<string, ReportGenerationResult> resultSelector)
    {
        _reportServiceMock
            .Setup(s => s.CreateMonthlyReportAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((string cardIdm, int year, int month, string outputPath) =>
            {
                _createdForCardIdms.Add(cardIdm);
                _createdRequests.Add((year, month, outputPath));
                return Task.FromResult(resultSelector(cardIdm));
            });
    }

    private CardDto SelectCard(string idm, string number)
    {
        var card = new CardDto
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = number,
            IsSelected = true
        };
        _viewModel.Cards.Add(card);
        _viewModel.SelectedCards.Add(card);
        return card;
    }

    /// <summary>
    /// 欠陥を突く側: 作成中に選択が解除されても、開始時点の対象を全件処理して完走すること
    /// </summary>
    /// <remarks>
    /// 修正前は 3 枚目の <c>SelectedCards.First(...)</c> が一致せず
    /// <see cref="InvalidOperationException"/> になり、
    /// <c>catch (OperationCanceledException)</c> では拾えないため一括作成全体が落ちていた。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_作成中に選択が解除されても開始時点の対象を全件作成して完走すること()
    {
        // Arrange
        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");
        var thirdCard = SelectCard("0000000000000003", "003");

        // 1 枚目の作成中に 3 枚目のチェックが外れる（キーボード操作を模す）
        SetupReportCreation(cardIdm =>
        {
            if (cardIdm == "0000000000000001")
            {
                _viewModel.SelectedCards.Remove(thirdCard);
            }

            return ReportGenerationResult.SuccessResult(cardIdm);
        });

        // Act
        await _viewModel.CreateReportAsync();

        // Assert - 3 枚とも作成され、件数表示も開始時点の 3 件で数える
        _createdForCardIdms.Should().Equal(
            new[] { "0000000000000001", "0000000000000002", "0000000000000003" },
            "開始時点の選択をスナップショットして、その並び順のまま処理するため");
        _viewModel.CreatedFiles.Should().HaveCount(3);
        _viewModel.StatusMessage.Should().Be("3件の帳票を作成しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// 欠陥を突く側: 作成中に選択が追加されても、開始時点の対象だけを作成すること
    /// </summary>
    /// <remarks>
    /// 修正前は件数表示だけが現在の <c>SelectedCards.Count</c> を見ていたため、
    /// 全件成功したのに「2/3件（一部失敗）」と誤って報告していた。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_作成中に選択が追加されてもスナップショット外のカードは作成しないこと()
    {
        // Arrange
        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");

        var lateCard = new CardDto
        {
            CardIdm = "0000000000000009",
            CardType = "はやかけん",
            CardNumber = "009",
            IsSelected = true
        };
        _viewModel.Cards.Add(lateCard);

        SetupReportCreation(cardIdm =>
        {
            if (cardIdm == "0000000000000001")
            {
                _viewModel.SelectedCards.Add(lateCard);
            }

            return ReportGenerationResult.SuccessResult(cardIdm);
        });

        // Act
        await _viewModel.CreateReportAsync();

        // Assert
        _createdForCardIdms.Should().Equal(
            new[] { "0000000000000001", "0000000000000002" });
        _viewModel.StatusMessage.Should().Be("2件の帳票を作成しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// 対の表明: 選択が変わらない通常経路では従来どおり全件作成して成功を表示すること
    /// </summary>
    /// <remarks>
    /// これが無いと、ループを丸ごと止めた実装や成功を決め打ちにした実装でも上の 2 件が緑になる。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_選択が変わらなければ全件作成して成功を表示すること()
    {
        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");

        await _viewModel.CreateReportAsync();

        _createdForCardIdms.Should().Equal(
            new[] { "0000000000000001", "0000000000000002" });
        _viewModel.CreatedFiles.Should().HaveCount(2);
        _viewModel.StatusMessage.Should().Be("2件の帳票を作成しました");
        _viewModel.IsStatusError.Should().BeFalse();
    }

    /// <summary>
    /// 対の表明: 一部失敗はスナップショットの件数を分母にして報告すること
    /// </summary>
    /// <remarks>
    /// 成功件数を決め打ちにした実装や、分母をスナップショット以外から採る実装を検出する。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_一部失敗時はスナップショット件数を分母に報告すること()
    {
        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");
        SelectCard("0000000000000003", "003");

        SetupReportCreation(cardIdm => cardIdm == "0000000000000002"
            ? ReportGenerationResult.FailureResult("帳票データを取得できませんでした")
            : ReportGenerationResult.SuccessResult(cardIdm));

        await _viewModel.CreateReportAsync();

        _viewModel.StatusMessage.Should().Be("2/3件の帳票を作成しました（一部失敗）");
        _viewModel.IsStatusError.Should().BeTrue();
        _navigationServiceMock.Verify(
            n => n.ShowWarning(It.Is<string>(m => m.Contains("はやかけん 002")), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// 欠陥を突く側: 作成中に対象年月が変えられても、開始時点の年月で全件作成すること
    /// </summary>
    /// <remarks>
    /// 年月コンボボックスはキーボードで操作でき、処理中オーバーレイはマウスしか塞がない（#1761）。
    /// ループの中で <c>SelectedYear</c> / <c>SelectedMonth</c> を引き直すと、旧年月で決めた
    /// 年度ファイル名（<c>fiscalYear</c>）と上書き確認で職員が同意した「N月のシートを更新する」に対し、
    /// 別の月・別の年度のシートを書き込むことになる（6 年保存の帳票が誤ったファイルへ入る）。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_作成中に対象年月が変えられても開始時点の年月で作成すること()
    {
        // Arrange
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 5;

        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");

        // 1 枚目の作成中に対象年月が変わる（キーボード操作を模す）
        SetupReportCreation(cardIdm =>
        {
            if (cardIdm == "0000000000000001")
            {
                _viewModel.SelectedYear = 2025;
                _viewModel.SelectedMonth = 11;
            }

            return ReportGenerationResult.SuccessResult(cardIdm);
        });

        // Act
        await _viewModel.CreateReportAsync();

        // Assert - 2 枚とも開始時点の年月で作成される
        _createdRequests.Select(r => (r.Year, r.Month)).Should().Equal(
            new[] { (2026, 5), (2026, 5) },
            "対象年月は作成開始時点のスナップショットから採るため");

        // 出力先も開始時点の年月から決めた年度ファイル（FY2026）のまま
        _createdRequests.Select(r => Path.GetFileName(r.OutputPath)).Should().OnlyContain(
            f => f.Contains("2026"),
            "年度ファイル名は開始時点の年月から決めるため");
    }

    /// <summary>
    /// 対の表明: 年月を変えなければ、その年月で作成すること
    /// </summary>
    /// <remarks>
    /// これが無いと、年月を定数へ決め打ちにした実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public async Task CreateReportAsync_年月が変わらなければ選択中の年月で作成すること()
    {
        _viewModel.SelectedYear = 2025;
        _viewModel.SelectedMonth = 11;

        SelectCard("0000000000000001", "001");

        await _viewModel.CreateReportAsync();

        _createdRequests.Select(r => (r.Year, r.Month)).Should().Equal(new[] { (2025, 11) });
    }

    /// <summary>
    /// プレビュー用データの応答を設定する（Issue #1949）
    /// </summary>
    /// <param name="onFirstRequest">1 件目の取得時に実行する副作用（選択変更の再現用）</param>
    private void SetupPreviewData(Action? onFirstRequest = null)
    {
        _printServiceMock
            .Setup(s => s.GetReportDataAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((string cardIdm, int year, int month) =>
            {
                _previewRequests.Add((cardIdm, year, month));
                if (_previewRequests.Count == 1)
                {
                    onFirstRequest?.Invoke();
                }

                return Task.FromResult<ReportPrintData?>(new ReportPrintData
                {
                    CardType = "はやかけん",
                    CardNumber = cardIdm,
                    Year = year,
                    Month = month
                });
            });
    }

    /// <summary>
    /// 欠陥を突く側: プレビュー生成中に対象年月が変わっても、開始時点の年月で全カードを取得すること
    /// </summary>
    /// <remarks>
    /// カードごとのデータ取得は await をまたぐため、毎周 <c>SelectedYear</c> / <c>SelectedMonth</c> を
    /// 読むと **1 つの結合ドキュメントに別々の月のデータが混在**する。印刷すれば月をまたいだ
    /// 物品出納簿がそのまま出力される。
    /// </remarks>
    [Fact]
    public async Task PreviewSelectedAsync_生成中に対象年月が変わっても開始時点の年月で全カードを取得すること()
    {
        // Arrange
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 7;
        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");
        SelectCard("0000000000000003", "003");

        // 1 枚目の取得中に対象年月が変わる（キーボード操作を模す）
        SetupPreviewData(onFirstRequest: () =>
        {
            _viewModel.SelectedYear = 2025;
            _viewModel.SelectedMonth = 3;
        });

        // Act
        await _viewModel.PreviewSelectedAsync();

        // Assert - 3 枚とも開始時点の 2026 年 7 月で取得される
        _previewRequests.Select(r => (r.Year, r.Month)).Should().Equal(
            new[] { (2026, 7), (2026, 7), (2026, 7) });
        _previewRequests.Select(r => r.CardIdm).Should().Equal(
            new[] { "0000000000000001", "0000000000000002", "0000000000000003" });
    }

    /// <summary>
    /// 対の表明: 対象年月が変わらなければ、現在の選択どおりの年月で取得すること
    /// </summary>
    /// <remarks>
    /// これが無いと、年月を固定値へ決め打ちにした実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public async Task PreviewSelectedAsync_対象年月が変わらなければ現在の選択どおりに取得すること()
    {
        _viewModel.SelectedYear = 2026;
        _viewModel.SelectedMonth = 7;
        SelectCard("0000000000000001", "001");
        SelectCard("0000000000000002", "002");

        await _viewModel.PreviewSelectedAsync();

        _previewRequests.Select(r => (r.Year, r.Month)).Should().Equal(new[] { (2026, 7), (2026, 7) });
    }
}
