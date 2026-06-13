using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1598: 設定ファイル保存（<see cref="SettingsViewModel.SaveConfigFile"/>）のアトミック書き込みテスト。
///
/// 旧実装は <c>File.Delete</c> → <c>File.Move</c> の2段階で、その間にクラッシュすると
/// 設定ファイルが消失していた。新実装は <c>File.Replace</c> による原子的置換に変更し、
/// 対象が存在しない初回のみ <c>File.Move</c> へフォールバックする。
///
/// クラッシュそのものを単体テストで再現するのは不可能なため、ここでは
/// 「両分岐（初回作成 / 既存置換）が正しい内容を書き込み、一時ファイルを残さない」
/// という観測可能な契約を固定する。アトミック性そのものは <c>File.Replace</c> の
/// セマンティクスに依存し、コードレビューで担保する。
/// </summary>
public class SettingsViewModelConfigFileSaveTests
{
    private static string CreateTempDir()
    {
        // テスト間衝突を避けるためユニークなサブディレクトリを使う
        var dir = Path.Combine(Path.GetTempPath(), "ICCardManagerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveConfigFile_FileDoesNotExist_CreatesFileWithContent()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        try
        {
            const string value = @"\\server\share\iccard.db";

            SettingsViewModel.SaveConfigFile(filePath, value);

            File.Exists(filePath).Should().BeTrue("初回作成では File.Move フォールバックでファイルが作られる");
            File.ReadAllText(filePath, Encoding.UTF8).Should().Be(value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveConfigFile_FileAlreadyExists_ReplacesContentAtomically()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        try
        {
            File.WriteAllText(filePath, @"C:\old\local\iccard.db", Encoding.UTF8);
            const string newValue = @"\\server\share\iccard.db";

            SettingsViewModel.SaveConfigFile(filePath, newValue);

            File.Exists(filePath).Should().BeTrue("置換後も対象ファイルは常に存在する（Delete→Move の消失窓がない）");
            File.ReadAllText(filePath, Encoding.UTF8).Should().Be(newValue, "既存ファイルが新しい値で置換される");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveConfigFile_DoesNotLeaveTempFile_OnCreate()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        try
        {
            SettingsViewModel.SaveConfigFile(filePath, @"\\server\share\iccard.db");

            File.Exists(filePath + ".tmp").Should().BeFalse("初回作成後に一時ファイルが残らない");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveConfigFile_DoesNotLeaveTempFile_OnReplace()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        try
        {
            File.WriteAllText(filePath, "old", Encoding.UTF8);

            SettingsViewModel.SaveConfigFile(filePath, @"\\server\share\iccard.db");

            File.Exists(filePath + ".tmp").Should().BeFalse("置換後に一時ファイルが残らない");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveConfigFile_NullValue_WritesEmptyFile()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        try
        {
            SettingsViewModel.SaveConfigFile(filePath, null!);

            File.Exists(filePath).Should().BeTrue("null 値でも空ファイルとして保存される");
            File.ReadAllText(filePath, Encoding.UTF8).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveConfigFile_JapaneseValue_RoundTripsAsUtf8()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "department_config.txt");
        try
        {
            const string value = "市長事務部局";

            SettingsViewModel.SaveConfigFile(filePath, value);

            File.ReadAllText(filePath, Encoding.UTF8).Should().Be(value, "日本語が UTF-8 で正しく往復する");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
