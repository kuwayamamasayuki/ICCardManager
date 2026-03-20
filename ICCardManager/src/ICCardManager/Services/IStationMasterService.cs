using ICCardManager.Common;

namespace ICCardManager.Services
{
    /// <summary>
    /// 駅コードから駅名を解決するサービスのインターフェース
    /// </summary>
    public interface IStationMasterService
    {
        void EnsureLoaded();
        string GetStationName(int stationCode);
        string GetStationName(int stationCode, CardType cardType);
        string GetStationNameByArea(int areaCode, int lineCode, int stationNum);
        string GetStationNameOrNull(int lineCode, int stationNum);
        string GetStationNameOrNull(int lineCode, int stationNum, CardType cardType);
        string GetLineName(int lineCode);
        string GetLineName(int lineCode, CardType cardType);
        int StationCount { get; }
        int LineCount { get; }
        int GetStationCountByArea(int areaCode);
    }
}
