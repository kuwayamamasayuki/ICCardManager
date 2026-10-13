#nullable enable
using System;
using System.Globalization;
using System.Threading;

namespace ICCardManager.Services
{
    /// <summary>
    /// リストア元ファイルの検証で不合格になった理由（Issue #2269）
    /// </summary>
    public enum RestoreSourceValidationFailure
    {
        /// <summary>合格</summary>
        None,

        /// <summary>ファイルが存在しない</summary>
        FileNotFound,

        /// <summary>リストア元のファイルを読み取れない（他のプログラムが開いている・権限が無い・共有フォルダーへ接続できない）</summary>
        Unreadable,

        /// <summary>検証用の一時コピーをこのパソコンの一時フォルダーへ書けない（空き容量の不足等）</summary>
        StagingFailed,

        /// <summary>SQLite データベースではない、または書き込み途中で切り詰められている（ヘッダの検査）</summary>
        NotSqliteDatabase,

        /// <summary>SQLite として開けるが、整合性検査（<c>PRAGMA quick_check</c>）に合格しない</summary>
        Corrupted,

        /// <summary>SQLite のデータベースだが、本システムの DB の印になる表（<c>staff</c> / <c>schema_migrations</c>）が無い</summary>
        NotApplicationDatabase,

        /// <summary>本システムが作成しないトリガー・ビュー・制約の衝突解決句を含む</summary>
        UnexpectedSchemaObject,

        /// <summary>版の記録（<c>schema_migrations</c>）が本システムの書き方では表せない値を持つ</summary>
        UnrecognizedSchemaVersion,

        /// <summary>このアプリより新しいスキーマ版で作られている</summary>
        NewerSchemaVersion
    }

    /// <summary>
    /// リストア元ファイルの検証結果（Issue #2269）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不合格の理由ごとに「何が／なぜ／どうすれば」を備えた案内文言を持つ。文言をここへ集めるのは、
    /// 判定（<see cref="BackupService.PrepareRestoreSource"/>）と表示（システム管理画面）の間で
    /// 理由と文言が食い違わないようにするため。
    /// </para>
    /// <para>
    /// 合格した結果は、検証に使った<b>一時コピー</b>を持つ（<see cref="StagedCopyPath"/>）。置き換えはこのコピーから行う
    /// （<see cref="BackupService.RestoreFromPreparedSource(RestoreSourceValidationResult)"/>）。検証した後にリストア元が差し替えられても、
    /// 検証していない内容で置き換えないため（TOCTOU）。あわせて、共有フォルダー上のリストア元を読むのが 1 回で済む。
    /// 一時コピーは <see cref="Dispose"/> で削除するので、呼び出し元は <c>using</c> で持つこと。
    /// </para>
    /// </remarks>
    public sealed class RestoreSourceValidationResult : IDisposable
    {
        private Action? _cleanup;

        private RestoreSourceValidationResult(
            RestoreSourceValidationFailure failure,
            string userMessage,
            string? stagedCopyPath = null,
            Action? cleanup = null,
            string? sourcePath = null)
        {
            Failure = failure;
            UserMessage = userMessage;
            StagedCopyPath = stagedCopyPath;
            SourcePath = sourcePath;
            _cleanup = cleanup;
        }

        /// <summary>合格した結果（一時コピーを持たない。テストのモックや、検証だけを行う呼び出し向け）</summary>
        public static RestoreSourceValidationResult Valid { get; } =
            new RestoreSourceValidationResult(RestoreSourceValidationFailure.None, string.Empty);

        /// <summary>不合格の理由（合格なら <see cref="RestoreSourceValidationFailure.None"/>）</summary>
        public RestoreSourceValidationFailure Failure { get; }

        /// <summary>合格したかどうか</summary>
        public bool IsValid => Failure == RestoreSourceValidationFailure.None;

        /// <summary>利用者へ示す案内（合格なら空文字）</summary>
        public string UserMessage { get; }

        /// <summary>検証に合格した一時コピーのパス（不合格・<see cref="Valid"/> では null）</summary>
        internal string? StagedCopyPath { get; }

        /// <summary>検証したリストア元のパス（ログ用。置き換えには使わない。不合格・<see cref="Valid"/> では null）</summary>
        internal string? SourcePath { get; }

        /// <summary>
        /// 一時コピーとそのジャーナルを削除する（2 回目以降は何もしない）
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _cleanup, null)?.Invoke();
        }

        internal static RestoreSourceValidationResult ValidWithStagedCopy(
            string stagedCopyPath, Action cleanup, string? sourcePath = null) => new(
            RestoreSourceValidationFailure.None, string.Empty, stagedCopyPath, cleanup, sourcePath);

        internal static RestoreSourceValidationResult FileNotFound() => new(
            RestoreSourceValidationFailure.FileNotFound,
            "リストアを中止しました。選択したバックアップファイルが見つかりません。" +
            "ファイルが移動・削除されていないか確認し、バックアップファイルを選び直してください。");

        internal static RestoreSourceValidationResult Unreadable() => new(
            RestoreSourceValidationFailure.Unreadable,
            "リストアを中止しました。選択したバックアップファイルを読み取れません" +
            "（他のプログラムで開かれている、読み取る権限が無い、または共有フォルダーに接続できない可能性があります）。" +
            "ファイルを開いているプログラムを閉じ、共有フォルダーに接続できることを確かめてから、もう一度リストアしてください。");

        internal static RestoreSourceValidationResult StagingFailed() => new(
            RestoreSourceValidationFailure.StagingFailed,
            "リストアを中止しました。検証用の一時コピーを、このパソコンの一時フォルダーに作れませんでした" +
            "（ディスクの空き容量の不足などが考えられます）。" +
            "このパソコンのディスクの空き容量を確保してから、もう一度リストアしてください。");

        internal static RestoreSourceValidationResult NotSqliteDatabase() => new(
            RestoreSourceValidationFailure.NotSqliteDatabase,
            "リストアを中止しました。選択したファイルは本システムのデータベースのバックアップではないか、" +
            "書き込み途中で途切れています。別のバックアップファイルを選択してください。");

        internal static RestoreSourceValidationResult Corrupted() => new(
            RestoreSourceValidationFailure.Corrupted,
            "リストアを中止しました。選択したバックアップファイルは破損しています" +
            "（データベースの整合性検査に合格しませんでした）。別のバックアップファイルを選択してください。");

        internal static RestoreSourceValidationResult NotApplicationDatabase() => new(
            RestoreSourceValidationFailure.NotApplicationDatabase,
            "リストアを中止しました。選択したファイルはデータベースですが、本システム（ピッすい）のデータベースではありません" +
            "（職員や版の記録の表がありません）。本システムのバックアップファイル（backup_….db）を選択してください。");

        internal static RestoreSourceValidationResult UnexpectedSchemaObject() => new(
            RestoreSourceValidationFailure.UnexpectedSchemaObject,
            "リストアを中止しました。選択したバックアップファイルのトリガー・ビュー・制約・列の定義などに、" +
            "本システムが作らない書き換えの形跡（書き込みを黙って捨てる・消す仕組み）があり、本システム以外の手段で書き換えられた可能性があります。" +
            "このファイルは使わずに IT 担当者に相談し、リストアするときはバックアップフォルダー内の別のバックアップファイルを選択してください。");

        internal static RestoreSourceValidationResult UnrecognizedSchemaVersion() => new(
            RestoreSourceValidationFailure.UnrecognizedSchemaVersion,
            "リストアを中止しました。選択したバックアップファイルのデータベースの版の記録が、本システムが書かない値になっており、" +
            "本システム以外の手段で書き換えられた可能性があります。" +
            "このファイルは使わずに IT 担当者に相談し、リストアするときはバックアップフォルダー内の別のバックアップファイルを選択してください。");

        internal static RestoreSourceValidationResult NewerSchemaVersion(int fileVersion, int appVersion) => new(
            RestoreSourceValidationFailure.NewerSchemaVersion,
            string.Format(
                CultureInfo.CurrentCulture,
                "リストアを中止しました。選択したバックアップファイルは、このアプリより新しいバージョンで作られています" +
                "（データベースの版 {0}、このアプリが扱える版 {1} まで）。リストアするとアプリが起動できなくなります。" +
                "アプリを新しいバージョンへ更新してから、もう一度リストアしてください。",
                fileVersion,
                appVersion));
    }
}
