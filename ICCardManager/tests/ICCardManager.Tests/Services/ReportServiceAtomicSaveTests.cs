using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2040: 年度ファイルを一時ファイル経由で差し替えることを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は <c>workbook.SaveAs(outputPath)</c> で年度ファイルへ直接上書き保存しており、保存が途中で
/// 中断すると zip が切れたファイルが最終名のまま残った。以後その年度の作成は開く段階で失敗し続け、
/// それまでの月のシートも失われる。
/// </para>
/// <para>
/// 回帰は「保存に失敗しても既存の内容が保たれる」側と「正常時は最終名のファイルだけが残る」側を対で置く。
/// 前者だけだと保存を丸ごと止めた実装でも緑になり、後者だけだと直接上書き保存（＝この欠陥）でも緑になる。
/// </para>
/// </remarks>
public class ReportServiceAtomicSaveTests : IDisposable
{
    private const string CardIdm = "0102030405060708";
    private const int Year = 2024;

    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly RecordingLogger<ReportService> _logger = new();
    private readonly string _directory;
    private readonly string _outputPath;

    public ReportServiceAtomicSaveTests()
    {
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(CardIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = CardIdm, CardType = "はやかけん", CardNumber = "H001" });

        _directory = Path.Combine(Path.GetTempPath(), $"ReportAtomicSave_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _outputPath = Path.Combine(_directory, "物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // 後始末の失敗はテスト結果に影響させない
        }
    }

    #region ヘルパー

    private ReportService CreateService()
    {
        return new ReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new ReportDataBuilder(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object),
            _logger);
    }

    private SeamReportService CreateSeamService()
    {
        return new SeamReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new ReportDataBuilder(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object),
            _logger);
    }

    private SeamReportService CreateFailingWriteService()
    {
        var service = CreateSeamService();
        // 保存の途中で中断する状況（共有フォルダーの切断・ディスク満杯）: zip の先頭だけを書いてから例外を投げる
        service.WriteHook = path =>
        {
            File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 });
            throw new IOException("simulated: network name is no longer available");
        };
        return service;
    }

    /// <summary>
    /// File.Replace が ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 で失敗した状況: 置換先（最終名）を消したまま例外になる
    /// </summary>
    private static void ReplaceThatRemovesDestination(string tempPath, string outputPath)
    {
        File.Delete(outputPath);
        throw new IOException("simulated: ERROR_UNABLE_TO_MOVE_REPLACEMENT_2");
    }

    private List<string> RecoveryFilesInDirectory()
    {
        var name = Path.GetFileNameWithoutExtension(_outputPath);
        return Directory.GetFiles(_directory, name + "_復旧_*.xlsx").ToList();
    }

    private static List<string> ReadSheetNames(string path)
    {
        // パス指定の XLWorkbook は拡張子で形式を判定し .tmp を拒否するため、ストリームで開く
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var workbook = new XLWorkbook(stream);
        return workbook.Worksheets.Select(w => w.Name).ToList();
    }

    private List<string> FileNamesInDirectory()
    {
        return Directory.GetFiles(_directory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList()!;
    }

    /// <summary>
    /// 実機でしか起きないファイル操作の失敗を差し替えて再現する。フックが null なら本番の実装を使う。
    /// </summary>
    private sealed class SeamReportService : ReportService
    {
        public SeamReportService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository,
            IReportDataBuilder reportDataBuilder,
            ILogger<ReportService> logger)
            : base(cardRepository, ledgerRepository, settingsRepository, reportDataBuilder, logger)
        {
        }

        public List<string> WrittenPaths { get; } = new();
        public Action<string>? WriteHook { get; set; }
        public Action<string, string>? ReplaceHook { get; set; }
        public Action<string, string>? MoveHook { get; set; }
        public Func<string, XLWorkbook>? OpenHook { get; set; }

        internal override void WriteWorkbookTo(XLWorkbook workbook, string path)
        {
            WrittenPaths.Add(path);
            if (WriteHook != null) { WriteHook(path); return; }
            base.WriteWorkbookTo(workbook, path);
        }

        internal override void ReplaceTempFile(string tempPath, string outputPath)
        {
            if (ReplaceHook != null) { ReplaceHook(tempPath, outputPath); return; }
            base.ReplaceTempFile(tempPath, outputPath);
        }

        internal override void MoveTempFile(string tempPath, string outputPath)
        {
            if (MoveHook != null) { MoveHook(tempPath, outputPath); return; }
            base.MoveTempFile(tempPath, outputPath);
        }

        internal override XLWorkbook OpenExistingWorkbook(string path)
        {
            return OpenHook != null ? OpenHook(path) : base.OpenExistingWorkbook(path);
        }
    }

    #endregion

    #region 正常時

    [Fact]
    public async Task 正常時_出力先には最終名の年度ファイルだけが残り既存の月のシートも保たれる()
    {
        var service = CreateService();

        var april = await service.CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath);
        var may = await service.CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);

        april.Success.Should().BeTrue(april.DetailedErrorMessage);
        may.Success.Should().BeTrue(may.DetailedErrorMessage);
        FileNamesInDirectory().Should().Equal(Path.GetFileName(_outputPath));
        ReadSheetNames(_outputPath).Should().Equal("4月", "5月");
    }

    #endregion

    #region 書き込み段の失敗

    [Fact]
    public async Task 既存の年度ファイルへの保存が途中で失敗しても_既存ファイルは保存前の内容のまま残る()
    {
        var aprilResult = await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath);
        aprilResult.Success.Should().BeTrue(aprilResult.DetailedErrorMessage);
        var bytesBefore = File.ReadAllBytes(_outputPath);

        var failing = CreateFailingWriteService();
        var result = await failing.CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);

        result.Success.Should().BeFalse();
        failing.WrittenPaths.Should().ContainSingle()
            .Which.Should().NotBe(_outputPath, "書き込みは最終名ではなく一時ファイルへ行う");
        File.ReadAllBytes(_outputPath).Should().Equal(bytesBefore, "最終名のファイルには一切触れない");
        ReadSheetNames(_outputPath).Should().Equal("4月");
        FileNamesInDirectory().Should().Equal(
            new[] { Path.GetFileName(_outputPath) }, "中断した一時ファイルは削除する");
    }

    [Fact]
    public async Task 新規の年度ファイルの保存が途中で失敗したら_最終名のファイルも一時ファイルも残らない()
    {
        var failing = CreateFailingWriteService();

        var result = await failing.CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath);

        result.Success.Should().BeFalse();
        File.Exists(_outputPath).Should().BeFalse("テンプレートの複製を最終名で残さない");
        FileNamesInDirectory().Should().BeEmpty();
    }

    #endregion

    #region 差し替え段の失敗

    [Fact]
    public async Task 年度ファイルが他のアプリケーションで開かれていて差し替えられないとき_既存の内容が保たれ一時ファイルは残らない()
    {
        var service = CreateService();
        (await service.CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath)).Success.Should().BeTrue();
        var bytesBefore = File.ReadAllBytes(_outputPath);

        ReportGenerationResult result;
        // Excel は開いているファイルの削除・置換を許さない（読み取りは許す）
        using (new FileStream(_outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await service.CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);
        }

        result.Success.Should().BeFalse();
        result.DetailedErrorMessage.Should().Contain("他のアプリケーションで開かれている");
        File.ReadAllBytes(_outputPath).Should().Equal(bytesBefore);
        FileNamesInDirectory().Should().Equal(
            new[] { Path.GetFileName(_outputPath) },
            "最終名のファイルが残っているなら一時ファイルは作り直せる出力にすぎず、残すと溜まる");
    }

    [Fact]
    public async Task 差し替えに失敗し最終名のファイルも無いとき_作成した内容を復旧用ファイルに残して名前を案内する()
    {
        // 最終名の位置にフォルダーがあると移動できない（最終名のファイルは存在しない）状況を作る
        Directory.CreateDirectory(_outputPath);

        var result = await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath);

        result.Success.Should().BeFalse();
        var recoveryFiles = RecoveryFilesInDirectory();
        recoveryFiles.Should().ContainSingle("移動先も一時ファイルも無い状態にしない");
        ReadSheetNames(recoveryFiles[0]).Should().Equal("4月");
        result.DetailedErrorMessage.Should().Contain(Path.GetFileName(recoveryFiles[0]))
            .And.Contain("名前を「" + Path.GetFileName(_outputPath) + "」に変えてから");
        Directory.GetFiles(_directory)
            .Where(p => ReportService.IsTempFileNameOf(Path.GetFileName(p), Path.GetFileName(_outputPath)))
            .Should().BeEmpty("回収対象の一時ファイルのままにしない（24 時間後に消える）");
        _logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(recoveryFiles[0]));
    }

    [Fact]
    public async Task 置換が最終名を消したまま失敗しても_作成した内容で年度ファイルを復元して成功する()
    {
        (await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath)).Success.Should().BeTrue();
        var service = CreateSeamService();
        service.ReplaceHook = ReplaceThatRemovesDestination;

        var result = await service.CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);

        result.Success.Should().BeTrue(result.DetailedErrorMessage);
        ReadSheetNames(_outputPath).Should().Equal(new[] { "4月", "5月" }, "一時ファイルは過去の月を含む完全な内容");
        FileNamesInDirectory().Should().Equal(Path.GetFileName(_outputPath));
    }

    [Fact]
    public async Task 置換が最終名を消し復元もできないとき_復旧用ファイルに残し_次回の作成でも消さない()
    {
        (await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath)).Success.Should().BeTrue();
        var service = CreateSeamService();
        service.ReplaceHook = ReplaceThatRemovesDestination;
        service.MoveHook = (_, _) => throw new IOException("simulated: move failed");

        var result = await service.CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);

        result.Success.Should().BeFalse();
        var recoveryFiles = RecoveryFilesInDirectory();
        recoveryFiles.Should().ContainSingle();
        ReadSheetNames(recoveryFiles[0]).Should().Equal("4月", "5月");
        result.DetailedErrorMessage.Should().Contain(Path.GetFileName(recoveryFiles[0]));

        // 次回の作成は年度ファイルが無いのでテンプレートから作り直すが、復旧用ファイルは回収しない
        File.SetLastWriteTime(recoveryFiles[0], DateTime.Now.AddHours(-(AppConstants.ReportTempFileStaleHours + 1)));
        (await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 6, _outputPath)).Success.Should().BeTrue();
        File.Exists(recoveryFiles[0]).Should().BeTrue("過去の月を含む唯一の内容を自動で消さない");
    }

    #endregion

    #region 一時ファイルの名前と回収

    [Fact]
    public void 一時ファイル名は年度ファイルの検索パターンに一致しない()
    {
        var fileName = Path.GetFileName(_outputPath);
        var tempPath = ReportService.BuildTempFilePath(_outputPath, "0123abcd");
        File.WriteAllText(_outputPath, "final");
        File.WriteAllText(tempPath, "temp");

        // Win32 のワイルドカード照合（8.3 短縮名を含む）を推論で済ませず、実際の列挙結果で固定する
        Directory.GetFiles(_directory, "*.xlsx").Select(Path.GetFileName).Should().Equal(fileName);
        ReportService.IsTempFileNameOf(Path.GetFileName(tempPath), fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("物品出納簿_はやかけん_H001_2024年度.xlsx.tmp")]
    [InlineData("物品出納簿_はやかけん_H001_2024年度.xlsx.0123abc.tmp")]
    [InlineData("物品出納簿_はやかけん_H001_2024年度.xlsx.0123abcz.tmp")]
    [InlineData("物品出納簿_はやかけん_H001_2025年度.xlsx.0123abcd.tmp")]
    [InlineData("物品出納簿_はやかけん_H001_2024年度.xlsx.0123abcd.tmpx")]
    [InlineData("other.0123abcd.tmp")]
    public void 他の年度ファイルや他のアプリケーションの一時ファイルは回収対象と判定しない(string candidate)
    {
        ReportService.IsTempFileNameOf(candidate, Path.GetFileName(_outputPath)).Should().BeFalse();
    }

    [Fact]
    public async Task 十分に古いこの年度ファイルの一時ファイルだけを回収する()
    {
        var fileName = Path.GetFileName(_outputPath);
        var old = DateTime.Now.AddHours(-(AppConstants.ReportTempFileStaleHours + 1));

        var staleOwn = ReportService.BuildTempFilePath(_outputPath, "aaaaaaaa");
        var freshOwn = ReportService.BuildTempFilePath(_outputPath, "bbbbbbbb");
        var staleOtherYear = ReportService.BuildTempFilePath(
            Path.Combine(_directory, "物品出納簿_はやかけん_H001_2025年度.xlsx"), "cccccccc");
        var staleUnrelated = Path.Combine(_directory, fileName + ".backup.tmp");

        foreach (var path in new[] { staleOwn, freshOwn, staleOtherYear, staleUnrelated })
        {
            File.WriteAllText(path, "x");
        }
        foreach (var path in new[] { staleOwn, staleOtherYear, staleUnrelated })
        {
            File.SetLastWriteTime(path, old);
        }

        var result = await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath);

        result.Success.Should().BeTrue(result.DetailedErrorMessage);
        File.Exists(staleOwn).Should().BeFalse("前回中断した保存の一時ファイルは回収する");
        File.Exists(freshOwn).Should().BeTrue("他 PC が作成中かもしれない新しい一時ファイルは消さない");
        File.Exists(staleOtherYear).Should().BeTrue("他の年度ファイルの一時ファイルはこの作成の対象外");
        File.Exists(staleUnrelated).Should().BeTrue("形式の合わないファイルは消さない");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains(staleOwn));
    }

    #endregion

    #region 既に壊れている年度ファイル

    [Fact]
    public async Task 既存の年度ファイルが壊れているとき_原因と回復手段を示し_ファイルを書き換えない()
    {
        var corrupted = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 };
        File.WriteAllBytes(_outputPath, corrupted);

        var result = await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("既存の帳票ファイルを開けません");
        result.DetailedErrorMessage.Should().Contain(Path.GetFileName(_outputPath))
            .And.Contain("壊れている")
            .And.Contain("移動するか名前を変えてから、もう一度作成してください");
        File.ReadAllBytes(_outputPath).Should().Equal(corrupted, "壊れたファイルを黙って作り直さない");
    }

    [Fact]
    public async Task ClosedXMLが読めない正常な年度ファイルは_壊れていると案内しない()
    {
        (await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath)).Success.Should().BeTrue();
        var bytesBefore = File.ReadAllBytes(_outputPath);
        var service = CreateSeamService();
        // 職員が図形等を追加し、ClosedXML が対応しない要素で読み込みに失敗した状況
        service.OpenHook = _ => throw new NotSupportedException("simulated: unsupported drawing");

        var result = await service.CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("帳票の作成に失敗しました");
        result.DetailedErrorMessage.Should().NotContain("壊れている");
        File.ReadAllBytes(_outputPath).Should().Equal(bytesBefore);
    }

    [Fact]
    public async Task 既存の年度ファイルが排他ロックで開けないときは_壊れていると案内しない()
    {
        (await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 4, _outputPath)).Success.Should().BeTrue();

        ReportGenerationResult result;
        using (new FileStream(_outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await CreateService().CreateMonthlyReportAsync(CardIdm, Year, 5, _outputPath);
        }

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("ファイルの保存に失敗しました");
        result.DetailedErrorMessage.Should().NotContain("壊れている");
        ReadSheetNames(_outputPath).Should().Equal("4月");
    }

    #endregion
}
