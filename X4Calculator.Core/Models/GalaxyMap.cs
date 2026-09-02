namespace X4Calculator.Core.Models;

/// <summary>
/// 三维坐标（游戏世界单位，米）。
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>两点间欧氏距离。</summary>
    public double DistanceTo(Vec3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// 星门 / 轨道加速器类型。
/// </summary>
public enum GateKind
{
    /// <summary>普通星门（props_gates_anc_gate_macro）。</summary>
    Gate,

    /// <summary>轨道加速器（props_gates_orb_accelerator_01_macro）。</summary>
    Accelerator
}

/// <summary>
/// 星区（Cluster），对应 galaxy.xml / clusters.xml 中的 Cluster_NN_macro。
/// </summary>
public class ClusterInfo
{
    /// <summary>Cluster macro ID，如 "Cluster_01_macro"。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>英文名（解析 mapdefaults.xml 文本引用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属势力（从 god.xml 的 station 分布推断）。</summary>
    public string Owner { get; set; } = "ownerless";

    /// <summary>势力颜色（OWNER_COLORS 映射，用于星图着色）。</summary>
    public string OwnerColor { get; set; } = "#4b5563";

    /// <summary>银河系绝对坐标（来自 galaxy.xml 的 offset）。</summary>
    public Vec3 WorldPos { get; set; }

    /// <summary>本星区包含的扇区 macro ID 列表。</summary>
    public List<string> SectorIds { get; set; } = new();

    /// <summary>
    /// 星图显示坐标 X（游戏内星图布局，由世界坐标经轴向网格映射，屏幕 x 向右为正）。
    /// 由 <see cref="Data.StarMapDB.ComputeLayout"/> 计算。
    /// </summary>
    public double DisplayX { get; set; }

    /// <summary>
    /// 星图显示坐标 Y（屏幕 y 向下为正，与银河系 z 轴符号相反）。
    /// </summary>
    public double DisplayY { get; set; }

    /// <summary>
    /// 星区大六边形外接半径（星图显示单位，= 全局 clusterRadius）。
    /// 多扇区星区用它画出包裹全部扇区的轮廓六边形。
    /// </summary>
    public double DisplayRadius { get; set; }
}

/// <summary>
/// 扇区（Sector），对应 clusters.xml / sectors.xml 中的 Cluster_NN_SectorNNN_macro。
/// </summary>
public class SectorInfo
{
    /// <summary>Sector macro ID，如 "Cluster_01_Sector001_macro"。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属 Cluster macro ID。</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>英文名（解析 mapdefaults.xml 文本引用）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 搜索框与产线编排使用的双语名，例如 "Asteroid Belt｜小行星带"。
    /// 星图绘制与路线明细仍使用 <see cref="Name"/>，以保持英文显示。
    /// </summary>
    public string SearchName { get; set; } = string.Empty;

    /// <summary>搜索候选的最终显示名；中文文本缺失时回退到英文名。</summary>
    public string SearchDisplayName => string.IsNullOrWhiteSpace(SearchName) ? Name : SearchName;

    /// <summary>
    /// 星图光照系数，来自 mapdefaults.xml 的 area@sunlight；1.0 表示 100%。
    /// 扇区未单独声明时继承所属 Cluster，数据仍缺失时回退到 1.0。
    /// </summary>
    public double SunlightFactor { get; set; } = 1.0;

    /// <summary>用于界面和空间站模型的整数光伏效率百分比。</summary>
    public int SunlightPercent => (int)Math.Round(SunlightFactor * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 本扇区在 mapdefaults.xml 中通过 worlds 引用到的星体最大人口总和；
    /// world@factor 会参与折算。未声明 worlds 时为 0。
    /// </summary>
    public long Population { get; set; }

    /// <summary>所属势力（从 god.xml 的 station 分布推断）。</summary>
    public string Owner { get; set; } = "ownerless";

    /// <summary>势力颜色（OWNER_COLORS 映射，用于星图着色）。</summary>
    public string OwnerColor { get; set; } = "#4b5563";

    /// <summary>存档标记的争夺状态；静态默认地图为 false。</summary>
    public bool IsContested { get; set; }

    /// <summary>银河系绝对坐标（cluster 位置 + cluster→sector offset）。</summary>
    public Vec3 WorldPos { get; set; }

    /// <summary>本扇区内的星门/加速器 ID 列表。</summary>
    public List<string> GateIds { get; set; } = new();

    /// <summary>星图显示坐标 X（游戏内星图布局）。由 <see cref="Data.StarMapDB.ComputeLayout"/> 计算。</summary>
    public double DisplayX { get; set; }

    /// <summary>星图显示坐标 Y（屏幕 y 向下为正）。</summary>
    public double DisplayY { get; set; }

    /// <summary>扇区六边形外接半径（星图显示单位，随 cluster 数量缩放）。</summary>
    public double DisplayRadius { get; set; }

    /// <summary>模板槽位名（single/upper/lower/left/center/right 等，调试用）。</summary>
    public string Slot { get; set; } = "single";
}

/// <summary>
/// 星门 / 轨道加速器（对应 zones.xml 中 zone 的 connection ref="gates"）。
/// </summary>
public class GateInfo
{
    /// <summary>
    /// 门连接名（全局唯一），如 "connection_ClusterGate001To004"。
    /// 命名编码了连接关系：ClusterGate{所在Cluster}To{目标Cluster}。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所在 Cluster macro ID。</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>所在 Sector macro ID。</summary>
    public string SectorId { get; set; } = string.Empty;

    /// <summary>所在 Zone macro ID。</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>连接目标 Cluster macro ID（从门命名解析）。</summary>
    public string TargetClusterId { get; set; } = string.Empty;

    /// <summary>门类型：普通星门 / 轨道加速器。</summary>
    public GateKind Kind { get; set; }

    /// <summary>银河系绝对坐标（cluster + sector + zone + gate 四层 offset 累加）。</summary>
    public Vec3 WorldPos { get; set; }

    /// <summary>门在 Zone 内的局部坐标。</summary>
    public Vec3 LocalPos { get; set; }

    /// <summary>存档中的门代码（如 "RSY-973"），未扫描到则为 null。</summary>
    public string? Code { get; set; }

    /// <summary>存档中是否扫描到该门实例。</summary>
    public bool FoundInSave { get; set; }

    /// <summary>目标的门 ID（存档 destination 校验时补全，如 "connection_ClusterGate004To001"）。</summary>
    public string? TargetGateId { get; set; }

    /// <summary>星图显示坐标 X（游戏内星图布局，由 sector 局部坐标归一化投影）。由 <see cref="Data.StarMapDB.ComputeLayout"/> 计算。</summary>
    public double DisplayX { get; set; }

    /// <summary>星图显示坐标 Y（屏幕 y 向下为正）。</summary>
    public double DisplayY { get; set; }
}

/// <summary>
/// 星区内扇区间连接（超级高速路），用于 Cluster 内部 Sector↔Sector 的快速移动。
/// 单向通行：SectorA 的入口（FromZone）→ SectorB 的出口（ToZone）。
/// 两两成对放置时为双向（每条各一个方向），仅一条时为单向。
/// </summary>
public class SectorLink
{
    /// <summary>连接 ID（sechighway connection 名，如 "SuperHighway001_Cluster_01_connection"）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属 Cluster macro ID。</summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>起点 Sector macro ID（入口所在扇区）。</summary>
    public string SectorAId { get; set; } = string.Empty;

    /// <summary>终点 Sector macro ID（出口所在扇区）。</summary>
    public string SectorBId { get; set; } = string.Empty;

    /// <summary>入口 Zone macro ID（SectorA 内，sechighway 入口 zone）。</summary>
    public string FromZoneId { get; set; } = string.Empty;

    /// <summary>出口 Zone macro ID（SectorB 内，sechighway 出口 zone）。</summary>
    public string ToZoneId { get; set; } = string.Empty;

    /// <summary>连接类型："sechighway"（超级高速路）。</summary>
    public string Kind { get; set; } = "sechighway";

    /// <summary>入口端点显示坐标 X（星图显示单位，由 <see cref="Data.StarMapDB.ComputeLayout"/> 计算）。</summary>
    public double DisplayFromX { get; set; }

    /// <summary>入口端点显示坐标 Y。</summary>
    public double DisplayFromY { get; set; }

    /// <summary>出口端点显示坐标 X。</summary>
    public double DisplayToX { get; set; }

    /// <summary>出口端点显示坐标 Y。</summary>
    public double DisplayToY { get; set; }

    /// <summary>
    /// 同 cluster 内相同扇区对（无序）上的超级高速路数量：2 = 双向成对，1 = 单向。
    /// </summary>
    public int LaneCount { get; set; }

    /// <summary>同扇区对内的车道序号（用于双向平行偏移）。</summary>
    public int LaneIndex { get; set; }

    /// <summary>入口端点银河绝对坐标（SectorA 内 FromZone 中心，方向 A→B）。由 <see cref="Data.StarMapDB.ComputeSectorLinkWorldPositions"/> 填充。</summary>
    public Vec3 EntranceWorldPos { get; set; }

    /// <summary>出口端点银河绝对坐标（SectorB 内 ToZone 中心，方向 A→B）。</summary>
    public Vec3 ExitWorldPos { get; set; }
}

/// <summary>
/// 存档中扫描到的门实例（用于动态门校验/补全）。
/// </summary>
public class SaveGateInstance
{
    /// <summary>门 connection 名（如 "connection_clustergate409to410"，存档小写）。</summary>
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>门代码（如 "RSY-973"）。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>门 macro（如 "props_gates_anc_gate_macro"）。</summary>
    public string Macro { get; set; } = string.Empty;

    /// <summary>运行时组件 id（如 "[0x1a0657b7]"）。</summary>
    public string ComponentId { get; set; } = string.Empty;

    /// <summary>所在 Zone macro ID（通过连接路径链解析）。</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>destination 指向的组件 id（目标门）。</summary>
    public string? DestinationComponentId { get; set; }
}
