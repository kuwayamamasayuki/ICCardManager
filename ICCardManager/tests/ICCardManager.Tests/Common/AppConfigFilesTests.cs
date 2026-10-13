using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1598: 設定ファイル保存（<see cref="AppConfigFiles.Write"/>）のアトミック書き込みテスト。
/// Issue #2266 で <c>SettingsViewModel</c> の静的メソッドから <c>Common/AppConfigFiles</c> へ移した（挙動は同じ）。
/// 読み取り（<see cref="AppConfigFiles.Read"/>）の文字コード判定もここで固定する。
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
public class AppConfigFilesTests
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

            AppConfigFiles.Write(filePath, value);

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

            AppConfigFiles.Write(filePath, newValue);

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
            AppConfigFiles.Write(filePath, @"\\server\share\iccard.db");

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

            AppConfigFiles.Write(filePath, @"\\server\share\iccard.db");

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
            AppConfigFiles.Write(filePath, null);

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

            AppConfigFiles.Write(filePath, value);

            File.ReadAllText(filePath, Encoding.UTF8).Should().Be(value, "日本語が UTF-8 で正しく往復する");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_FileDoesNotExist_ReturnsEmpty()
    {
        var dir = CreateTempDir();
        try
        {
            AppConfigFiles.Read(Path.Combine(dir, "missing.txt")).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #2266: 移設後も、インストーラー（BOM なし Shift_JIS）・本アプリ（UTF-8 BOM）・UTF-16 LE BOM の
    /// いずれで書かれた設定ファイルも読め、前後の空白を取り除くこと
    /// </summary>
    [Theory]
    [InlineData("shift_jis")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le-bom")]
    public void Read_DecodesByBom_AndTrims(string encodingName)
    {
        const string value = @"\\サーバー\共有\帳票";
        byte[] bytes;
        switch (encodingName)
        {
            case "shift_jis":
                bytes = Encoding.GetEncoding(932).GetBytes("  " + value + "\r\n");
                break;
            case "utf-8-bom":
                bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(value + "\r\n")).ToArray();
                break;
            default:
                bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(value + "  ")).ToArray();
                break;
        }

        var dir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(dir, "config.txt");
            File.WriteAllBytes(filePath, bytes);

            AppConfigFiles.Read(filePath).Should().Be(value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(dir, "report_output_config.txt");
            AppConfigFiles.Write(filePath, @"D:\物品出納簿");

            AppConfigFiles.Read(filePath).Should().Be(@"D:\物品出納簿");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #2261: 別の職員が残した一時ファイル（<c>*.tmp</c>）があっても置き換えられること。
    /// </summary>
    /// <remarks>
    /// データフォルダーの中身には Users の「変更」しか無く、別の職員が作ったファイルに対してはアクセス許可の変更
    /// （WRITE_DAC）を持たない。その一時ファイルを書き直して使い回すと、<c>File.Replace</c>（置換先の ACL を写す）が
    /// 失敗する。別のユーザーを用意できないので、OWNER RIGHTS に「変更」だけを与えて所有者の暗黙の WRITE_DAC を消し、
    /// 「自分にはアクセス許可の変更が無い一時ファイル」を作って再現する。
    /// </remarks>
    [Fact]
    public void SaveConfigFile_アクセス許可を変更できない一時ファイルが残っていても置き換えられること()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        var tempPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(filePath, "old", Encoding.UTF8);
            File.WriteAllText(tempPath, "stale", Encoding.UTF8);
            RemoveWriteDacFromOwner(tempPath);

            AppConfigFiles.Write(filePath, @"\\server\share\iccard.db");

            File.ReadAllText(filePath, Encoding.UTF8).Should().Be(@"\\server\share\iccard.db");
            File.Exists(tempPath).Should().BeFalse("置換後に一時ファイルが残らない");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #2261: 書き直せない一時ファイル（<c>*.tmp</c>）が残っていても、消して作り直して置き換えられること。
    /// </summary>
    /// <remarks>
    /// 一時ファイルを使い回さず、消して自分で作り直すことの表明。上のテストは「作り直し」と「ACL の引き継ぎの失敗を
    /// 無視する（ignoreMetadataErrors）」のどちらか一方だけでも緑になるため、作り直しの側だけを外した変異を
    /// こちらで検出する（ignoreMetadataErrors の側は、所有者の異なる置換先を非昇格で作れないので手動確認で担保する）。
    /// 読み取りと削除だけを許した一時ファイルで再現する。
    /// </remarks>
    [Fact]
    public void SaveConfigFile_書き直せない一時ファイルが残っていても作り直して置き換えられること()
    {
        var dir = CreateTempDir();
        var filePath = Path.Combine(dir, "database_config.txt");
        var tempPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(filePath, "old", Encoding.UTF8);
            File.WriteAllText(tempPath, "stale", Encoding.UTF8);
            SetOnlyReadAndDelete(tempPath);

            AppConfigFiles.Write(filePath, @"\\server\share\iccard.db");

            File.ReadAllText(filePath, Encoding.UTF8).Should().Be(@"\\server\share\iccard.db");
            File.Exists(tempPath).Should().BeFalse("置換後に一時ファイルが残らない");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>ファイルの ACL を「OWNER RIGHTS と Users に読み取りと削除だけ」にする（書き込みもアクセス許可の変更もできない）。</summary>
    private static void SetOnlyReadAndDelete(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        foreach (var sid in new[] { "S-1-3-4", "S-1-5-32-545" })
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(sid),
                System.Security.AccessControl.FileSystemRights.Read | System.Security.AccessControl.FileSystemRights.Delete,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        info.SetAccessControl(security);
    }

    /// <summary>ファイルの ACL を「OWNER RIGHTS と Users に変更だけ」にし、所有者の暗黙の WRITE_DAC を消す。</summary>
    private static void RemoveWriteDacFromOwner(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        foreach (var sid in new[] { "S-1-3-4", "S-1-5-32-545" })
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(sid),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        info.SetAccessControl(security);
    }
}
