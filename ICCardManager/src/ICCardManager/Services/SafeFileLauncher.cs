using System.Diagnostics;
using System.IO;
using ICCardManager.Common;

namespace ICCardManager.Services
{
    /// <summary>
    /// Issue #1465: <see cref="ISafeFileLauncher"/> の既定実装。
    /// </summary>
    public sealed class SafeFileLauncher : ISafeFileLauncher
    {
        /// <inheritdoc />
        public SafeFileLaunchResult LaunchFolder(string folderPath)
        {
            var validation = SafeFilePathValidator.ValidateFolder(folderPath);
            if (!validation.IsValid)
            {
                return SafeFileLaunchResult.Fail(validation.ErrorMessage);
            }

            if (!Directory.Exists(folderPath))
            {
                return SafeFileLaunchResult.Fail(
                    "指定されたフォルダが見つかりません。" +
                    "ネットワーク切断やフォルダ削除の可能性があるため、" +
                    $"パスを確認してください: {folderPath}");
            }

            // explorer.exe を直接起動することでシェル関連付けを経由しない（defense-in-depth）。
            // UseShellExecute=false により、FileName は実行ファイルとしてのみ解釈される。
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + folderPath + "\"",
                    UseShellExecute = false
                });
                return SafeFileLaunchResult.Ok();
            }
            catch (System.Exception ex)
            {
                // #1614 / #1817: 生の ex.Message は出さず、技術的詳細はログへ残す（本クラスは ILogger を持たない）
                ErrorDialogHelper.LogException(ex, "エクスプローラーの起動");
                return SafeFileLaunchResult.Fail(
                    // 「原因:」というラベルは具体的な原因を約束するが、ToReason は種別ごとの定型文で、
                    // ここへ届く Win32Exception / InvalidOperationException は default 分岐
                    //（「予期しない問題が発生しました。」）に落ちる。ラベルを外して素の一文として並べ、
                    // 実際の原因はログへ誘導する。
                    "エクスプローラーの起動に失敗しました。" +
                    ExceptionMessageFormatter.ToReason(ex) +
                    "エクスプローラーが利用可能な環境か確認してください。");
            }
        }

        /// <inheritdoc />
        public SafeFileLaunchResult LaunchFile(string filePath)
        {
            var validation = SafeFilePathValidator.ValidateFile(filePath);
            if (!validation.IsValid)
            {
                return SafeFileLaunchResult.Fail(validation.ErrorMessage);
            }

            if (!File.Exists(filePath))
            {
                return SafeFileLaunchResult.Fail(
                    "指定されたファイルが見つかりません。" +
                    "ファイルが削除・移動されたか、エクスポートが失敗した可能性があるため、" +
                    $"再エクスポートをお試しください。パス: {filePath}");
            }

            // .xlsx / .csv の関連付け（Excel 等）を起動するため UseShellExecute=true を使用。
            // Validator で拡張子を whitelist 済みのため、関連付け先は表計算アプリに限定される。
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                    Verb = "open"
                });
                return SafeFileLaunchResult.Ok();
            }
            catch (System.Exception ex)
            {
                ErrorDialogHelper.LogException(ex, "ファイルを開く");
                return SafeFileLaunchResult.Fail(
                    // 「原因:」ラベルを外す理由は上の OpenFolder と同じ
                    "ファイルを開けませんでした。" +
                    ExceptionMessageFormatter.ToReason(ex) +
                    "対応するアプリ（Excel 等）がインストールされているか確認してください。");
            }
        }
    }
}
