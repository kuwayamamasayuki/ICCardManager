using System;
using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// この PC の帳票出力先（report_output_config.txt）の読み書き（Issue #2266）
/// </summary>
public class ReportOutputFolderStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;

    public ReportOutputFolderStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ReportOutputFolderStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "report_output_config.txt");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない
        }
    }

    [Fact]
    public void Load_ファイルが無いときは空文字を返すこと()
    {
        new ReportOutputFolderStore(_configPath).Load().Should().BeEmpty();
    }

    [Fact]
    public void Save_保存した出力先を次に読めること()
    {
        const string folder = @"\\server\share\帳票";
        var store = new ReportOutputFolderStore(_configPath);

        store.Save(folder);

        new ReportOutputFolderStore(_configPath).Load().Should().Be(folder);
    }

    [Fact]
    public void Load_インストーラーが書いたShiftJISの出力先を読めること()
    {
        // インストーラー（Inno Setup の SaveStringToFile）は BOM なしの Shift_JIS で書く
        const string folder = @"D:\物品出納簿\2026年度";
        File.WriteAllBytes(_configPath, Encoding.GetEncoding(932).GetBytes(folder));

        new ReportOutputFolderStore(_configPath).Load().Should().Be(folder);
    }
}
