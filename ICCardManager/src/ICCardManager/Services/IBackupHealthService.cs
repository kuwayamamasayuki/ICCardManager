using System.Threading.Tasks;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// バックアップ健全性の取得・記録を担当するサービス（Issue #1689）
    /// </summary>
    public interface IBackupHealthService
    {
        /// <summary>
        /// バックアップの健全性情報を取得する
        /// </summary>
        /// <returns>
        /// 最終成功日時・世代数・空き容量等を集約した情報。
        /// 個々の項目が取得できない場合も例外は投げず、該当項目を null / 0 として返す
        /// （健全性表示のために画面が開けなくなる事態を避けるため）
        /// </returns>
        Task<BackupHealthInfo> GetHealthAsync();

        /// <summary>
        /// VACUUM を実行した PC 名を記録する（共有モードでどの PC が実施したかの追跡用）
        /// </summary>
        Task RecordVacuumMachineAsync();
    }
}
