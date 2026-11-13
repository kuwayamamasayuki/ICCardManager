using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2461: 職員証を差し替える前に出力した職員の CSV を取り込んでも、古い職員証を新しい職員として登録しないこと。
/// </summary>
/// <remarks>
/// <para>
/// 差し替えは古い職員証の行を消す（主キーの付け替え）ため、古い CSV の行は「IDm が未登録の職員」に見える。
/// そのまま登録すると、同じ氏名・職員番号の職員がもう 1 人でき、紛失した古い職員証がまた使えるようになる。
/// 取り込みは、IDm が未登録で同じ職員番号の職員が別の職員証で登録されている行をスキップし、プレビューはその理由を示す。
/// </para>
/// <para>
/// 回帰は「スキップする側」と「従来どおり登録・更新する側」を対で置く（スキップを無条件にした実装を通さない）。
/// </para>
/// </remarks>
public class CsvImportStaffReplacedCardTests : IDisposable
{
    private const string StaffHeader = "職員IDm,氏名,職員番号,備考";
    private const string OldIdm = "0123456789ABCDEF";
    private const string NewIdm = "FEDCBA9876543210";
    private const string OtherIdm = "1111222233334444";
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheService;
    private readonly StaffRepository _staffRepository;
    private readonly CardRepository _cardRepository;
    private readonly OperationLogger _operationLogger;

    public CsvImportStaffReplacedCardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportStaffReplacedCard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _dbContext = TestDbContextFactory.Create();
        _cacheService = new CacheService(NullLogger<CacheService>.Instance);
        _staffRepository = new StaffRepository(
            _dbContext, _cacheService, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
        _cardRepository = new CardRepository(
            _dbContext, _cacheService, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _operationLogger = new OperationLogger(
            new OperationLogRepository(_dbContext), new CurrentOperatorContext(new SystemClock()));
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        _dbContext.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private CsvImportService CreateService()
    {
        var validationServiceMock = new Mock<IValidationService>();
        validationServiceMock.Setup(v => v.ValidateStaffIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        return new CsvImportService(
            _cardRepository,
            _staffRepository,
            new LedgerRepository(_dbContext),
            validationServiceMock.Object,
            _dbContext,
            _cacheService,
            settingsRepositoryMock.Object,
            _operationLogger,
            NullLogger<CsvImportService>.Instance);
    }

    private string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(_testDirectory, $"staff_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, StaffHeader + "\n" + string.Join("\n", lines) + "\n", CsvEncoding);
        return path;
    }

    /// <summary>博多 花子（S-001）の職員証を古い IDm から新しい IDm へ本物の差し替えで替えた状態にする</summary>
    private async Task SeedReplacedStaffAsync()
    {
        (await _staffRepository.InsertAsync(new Staff { StaffIdm = OldIdm, Name = "博多 花子", Number = "S-001" }))
            .Should().BeTrue();
        var service = new StaffManagementService(
            _dbContext, _staffRepository, _operationLogger, NullLogger<StaffManagementService>.Instance);
        (await service.ReplaceCardAsync(OldIdm, NewIdm)).IsOk.Should().BeTrue("前提: 職員証を差し替えた");
    }

    private async Task<long> CountStaffAsync(string idm)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM staff WHERE staff_idm = @idm";
        command.Parameters.AddWithValue("@idm", idm);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task 差し替える前の古い職員証の行はプレビューでスキップと示し理由を添えること()
    {
        await SeedReplacedStaffAsync();
        var csv = WriteCsv($"{OldIdm},博多 花子,S-001,");

        var preview = await CreateService().PreviewStaffAsync(csv);

        preview.IsValid.Should().BeTrue();
        preview.NewCount.Should().Be(0, "古い職員証を新しい職員として示さない");
        preview.SkipCount.Should().Be(1);
        var item = preview.Items.Should().ContainSingle().Subject;
        item.Action.Should().Be(ImportAction.Skip);
        item.Changes.Should().ContainSingle(c => c.IsDisplayOnly
            && c.NewValue!.Contains("職員番号 S-001")
            && c.NewValue.Contains("博多 花子")
            && c.NewValue.Contains("別の職員証で登録済み"));
    }

    [Fact]
    public async Task 差し替える前の古い職員証の行は取り込みで登録しないこと()
    {
        await SeedReplacedStaffAsync();
        var csv = WriteCsv($"{OldIdm},博多 花子,S-001,");

        var result = await CreateService().ImportStaffAsync(csv);

        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        (await CountStaffAsync(OldIdm)).Should().Be(0L, "紛失した古い職員証がまた使えるようにならない");
        (await CountStaffAsync(NewIdm)).Should().Be(1L);
    }

    [Fact]
    public async Task 職員番号が別の職員と一致しない未登録の行は従来どおり登録すること()
    {
        await SeedReplacedStaffAsync();
        var csv = WriteCsv($"{OtherIdm},天神 次郎,S-002,");

        var preview = await CreateService().PreviewStaffAsync(csv);
        var result = await CreateService().ImportStaffAsync(csv);

        preview.NewCount.Should().Be(1);
        result.ImportedCount.Should().Be(1);
        (await CountStaffAsync(OtherIdm)).Should().Be(1L);
    }

    [Fact]
    public async Task 職員番号が空の未登録の行は照合せず従来どおり登録すること()
    {
        await SeedReplacedStaffAsync();
        await _staffRepository.UpdateAsync(new Staff { StaffIdm = NewIdm, Name = "博多 花子", Number = null });
        var csv = WriteCsv($"{OtherIdm},天神 次郎,,");

        var result = await CreateService().ImportStaffAsync(csv);

        result.ImportedCount.Should().Be(1, "職員番号が空どうしは同じ職員の根拠にならない");
        (await CountStaffAsync(OtherIdm)).Should().Be(1L);
    }

    [Fact]
    public async Task 差し替えた後の職員証の行は従来どおり更新できること()
    {
        await SeedReplacedStaffAsync();
        var csv = WriteCsv($"{NewIdm},博多 花子（改姓）,S-001,");

        var result = await CreateService().ImportStaffAsync(csv);

        result.ImportedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(1, "同じ職員番号でも、同じ職員証の行はその職員の更新");
        (await _staffRepository.GetByIdmAsync(NewIdm))!.Name.Should().Be("博多 花子（改姓）");
    }

    [Fact]
    public async Task 同じ職員番号の職員が削除済みなだけなら従来どおり登録すること()
    {
        (await _staffRepository.InsertAsync(new Staff { StaffIdm = NewIdm, Name = "退職 三郎", Number = "S-001" }))
            .Should().BeTrue();
        (await _staffRepository.DeleteAsync(NewIdm)).Should().BeTrue();
        var csv = WriteCsv($"{OtherIdm},後任 四郎,S-001,");

        var result = await CreateService().ImportStaffAsync(csv);

        result.ImportedCount.Should().Be(1, "職員番号を引き継いだ後任の登録を止めない（照合するのは登録済みの職員だけ）");
        (await CountStaffAsync(OtherIdm)).Should().Be(1L);
    }

    [Theory]
    [InlineData("S-001", "S-001", true)]
    [InlineData(" S-001 ", "S-001", true)]
    [InlineData("S-002", "S-001", false)]
    [InlineData("", "S-001", false)]
    [InlineData(null, "S-001", false)]
    public void 判定_同じ職員番号で別の職員証の登録済みの職員を返す(string? rowNumber, string existingNumber, bool expected)
    {
        var active = new[] { new Staff { StaffIdm = NewIdm, Name = "博多 花子", Number = existingNumber } };

        CsvImportService.FindActiveStaffWithSameNumber(active, OldIdm, rowNumber)
            .Should().Match<Staff?>(s => (s != null) == expected);
    }

    [Fact]
    public void 判定_同じ職員証の行は同じ職員番号でも返さない()
    {
        var active = new[] { new Staff { StaffIdm = NewIdm, Name = "博多 花子", Number = "S-001" } };

        CsvImportService.FindActiveStaffWithSameNumber(active, NewIdm, "S-001").Should().BeNull();
    }
}
