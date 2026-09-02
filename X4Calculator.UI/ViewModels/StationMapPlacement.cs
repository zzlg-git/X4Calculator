using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

/// <summary>
/// 星图放置计划空间站时，由产线编排提供给星图视图的会话状态与提交入口。
/// 计划站覆盖层与存档导入的 PlayerStations 保持独立。
/// </summary>
public interface IStationMapPlacementSource
{
    bool IsPlacementActive { get; }
    Station? PendingPlacementStation { get; }
    IReadOnlyList<Station> PlacedPlannedStations { get; }
    event EventHandler? PlacementStateChanged;

    void CompletePlacement(SectorInfo sector, Vec3 sectorPosition, double displayX, double displayY);
    void RejectPlacement();
}

public enum StationMapPlacementNavigationTarget
{
    StarMap,
    StationPlanning
}

public sealed class StationMapPlacementNavigationEventArgs(
    StationMapPlacementNavigationTarget target) : EventArgs
{
    public StationMapPlacementNavigationTarget Target { get; } = target;
}
