using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Logging;

/// <summary>
/// Issue #2270: ファイルログのメッセージに含まれる改行で偽のレコードを差し込めないこと。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装はメッセージと例外メッセージを無加工で書き出しており、DB 由来の値（共有 DB の
/// <c>settings.last_backup_success_at</c> 等。<c>BackupHealthService</c> が警告ログへ埋め込む）に
/// <c>"\r\n[2026-…] [INF] [LendingService] 偽の行"</c> を入れると、行頭から始まる偽のレコードが現れた。
/// </para>
/// <para>
/// 欠陥を突く側はロガーを実際にファイルへ書かせて検査する（修正前のコードにもそのまま当てられる）。
/// ファイルの読み取りはテキストエディターが改行として表示し得る文字（NEL・U+2028・U+2029 を含む）で
/// 行に分ける — <c>File.ReadAllLines</c> は CR・LF でしか分けないため、U+2028 による差し込みを見逃す。
/// </para>
/// </remarks>
public class FileLoggerLineBreakTests : IDisposable
{
    private const string Category = "ICCardManager.Services.BackupHealthService";

    /// <summary>レコードの先頭行（<c>[日時] [レベル] [カテゴリ]</c>）</summary>
    private static readonly Regex RecordHeader = new(
        @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[[A-Z?]{3}\] \[[^\]]+\] ",
        RegexOptions.CultureInvariant);

    private static readonly string[] LineBreaks =
    {
        "\r\n", "\r", "\n", "\u0085", "\u2028", "\u2029", "\u000B", "\u000C",
    };

    private readonly string _logDirectory;

    public FileLoggerLineBreakTests()
    {
        // Issue #2098: テストプロセスでは AppDataPaths.RootDirectory が一時フォルダーへ差し替わっている
        _logDirectory = Path.Combine(AppDataPaths.RootDirectory, $"LineBreakLogs_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, true);
            }
        }
        catch
        {
            // テスト後のクリーンアップ失敗は無視
        }
    }

    #region 欠陥を突く側（ファイルへ実際に書かせる）

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void Log_DB由来の値に改行と偽のレコード先頭が入っていても1件のレコードとして書くこと(string lineBreak)
    {
        // BackupHealthService.ParseTimestamp の警告ログと同じ形（構造化ログのプレースホルダーへ生の値を渡す）
        var forged = $"x{lineBreak}[2026-01-01 00:00:00.000] [INF] [LendingService] 偽の行";

        var lines = WriteAndReadLines(logger =>
            logger.LogWarning("設定値を日時として解釈できませんでした: {Key}={Value}", "last_backup_success_at", forged));

        lines.Count(line => RecordHeader.IsMatch(line)).Should().Be(1,
            "埋め込まれた値が行頭に偽のレコードを作ってはならない");
        lines.Should().NotContain(line => line.StartsWith("[2026-01-01"),
            "偽のレコード先頭は行頭に現れてはならない");
        lines.Should().Contain("  | " + "[2026-01-01 00:00:00.000] [INF] [LendingService] 偽の行",
            "改行の後ろは継続行として残し、情報は失わない");
        lines[0].Should().EndWith("last_backup_success_at=x");
    }

    [Fact]
    public void Log_例外メッセージの改行も継続行にすること()
    {
        var lines = WriteAndReadLines(logger =>
            logger.LogError(
                new InvalidOperationException("SQL logic error\n[2026-01-01 00:00:00.000] [CRT] [DbContext] 偽の行"),
                "保存に失敗しました"));

        lines.Count(line => RecordHeader.IsMatch(line)).Should().Be(1);
        lines.Should().Equal(
            lines[0],
            "  Exception: InvalidOperationException: SQL logic error",
            "    | " + "[2026-01-01 00:00:00.000] [CRT] [DbContext] 偽の行");
    }

    [Fact]
    public void Log_改行以外の制御文字はエスケープ表記で書くこと()
    {
        // ESC[2J は端末で画面を消去するエスケープシーケンス、U+001E は str.splitlines が改行として扱う
        var value = "x" + (char)0x1B + "[2J" + (char)0x1E + "[2026-01-01 00:00:00.000] [INF] 偽";

        var lines = WriteAndReadLines(logger =>
            logger.LogWarning("設定値を日時として解釈できませんでした: {Key}={Value}", "last_backup_success_at", value));

        lines.Should().ContainSingle();
        lines[0].Should().EndWith(@"last_backup_success_at=x\u001B[2J\u001E[2026-01-01 00:00:00.000] [INF] 偽");
        lines[0].Any(c => c < ' ' && c != '\t').Should().BeFalse("制御文字をそのまま書き出さない");
    }

    #endregion

    #region 正当な出力を変えていない側

    [Fact]
    public void Log_改行を含まないメッセージは従来どおり1行で書くこと()
    {
        var lines = WriteAndReadLines(logger =>
            logger.LogInformation("バックアップを作成しました: {Path}", @"C:\ProgramData\ICCardManager\backup\backup_20261013.db"));

        lines.Should().HaveCount(1);
        RecordHeader.IsMatch(lines[0]).Should().BeTrue();
        lines[0].Should().EndWith(
            @"[BackupHealthService] バックアップを作成しました: C:\ProgramData\ICCardManager\backup\backup_20261013.db");
    }

    [Fact]
    public void Log_スタックトレースは従来どおり字下げして書くこと()
    {
        Exception thrown;
        try
        {
            ThrowForStackTrace();
            throw new InvalidOperationException("到達しない");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        var lines = WriteAndReadLines(logger => logger.LogError(thrown, "処理に失敗しました"));

        // スタックトレースの各行（"   at …" / 日本語環境では "   場所 …"）は OS の表示言語で変わるため、
        // 行の中身は例外自身のスタックトレースから取り、字下げの形だけを固定する
        var frames = thrown.StackTrace!.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        frames.Should().HaveCountGreaterThan(1, "2 行目以降の字下げを検査できるだけのフレームがあること");

        lines.Count(line => RecordHeader.IsMatch(line)).Should().Be(1);
        lines[1].Should().Be("  Exception: InvalidOperationException: スタックトレース検証");
        lines[2].Should().Be("  StackTrace: " + frames[0]);
        lines[2].Should().Contain(nameof(ThrowForStackTrace));
        lines.Skip(3).Should().Equal(frames.Skip(1).Select(frame => "    " + frame),
            "スタックトレースの 2 行目以降は従来どおり 4 文字の字下げ");
        lines.Should().NotContain(line => line.Contains("| "),
            "改行を含まないメッセージ・例外メッセージに継続行の接頭辞を付けない");
    }

    [Fact]
    public void FormatLogEntry_通常のメッセージと例外の書式が変わらないこと()
    {
        var entry = FileLogger.FormatLogEntry(
            new DateTime(2026, 10, 13, 9, 8, 7, 123),
            LogLevel.Warning,
            Category,
            "通常のメッセージ",
            new InvalidOperationException("失敗"));

        entry.Should().Be(
            "[2026-10-13 09:08:07.123] [WRN] [BackupHealthService] 通常のメッセージ" + Environment.NewLine +
            "  Exception: InvalidOperationException: 失敗");
    }

    #endregion

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowForStackTrace()
    {
        throw new InvalidOperationException("スタックトレース検証");
    }

    private string[] WriteAndReadLines(Action<ILogger> write)
    {
        var options = Options.Create(new FileLoggerOptions
        {
            Enabled = true,
            Path = new DirectoryInfo(_logDirectory).Name,
        });

        // Dispose がキューを排出してから終了する（Issue #1732）ので、Dispose 後に読めば書き込みは完了している
        using (var provider = new FileLoggerProvider(options))
        {
            write(provider.CreateLogger(Category));
        }

        var logFiles = Directory.GetFiles(_logDirectory, "ICCardManager_*.log");
        logFiles.Should().ContainSingle();
        var content = File.ReadAllText(logFiles[0]);

        return content
            .Split(LineBreaks, StringSplitOptions.None)
            .Where(line => line.Length > 0)
            .ToArray();
    }
}
