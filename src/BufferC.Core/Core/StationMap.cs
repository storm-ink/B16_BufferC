namespace BufferC.Core.Core;

/// <summary>
/// 站口物理↔逻辑映射（2026-08-20 现场口径）：PLC 物理布线为蛇形——左列奇槽、右列偶槽；
/// MCS/AGVC 的逻辑编号按列自上而下（16 站：左列 1..8、右列 9..16；8 站：左列 1..4、右列 5..8）。
/// 全系统（事件/台账/命名/cmsIndex/前端）用逻辑号；只有碰 PLC 寄存器的边界层调用本映射换算物理地址。
/// 公式：物理奇数 p → 逻辑 (p+1)/2；物理偶数 p → 逻辑 p/2 + 站数/2。
///       逻辑 L ≤ 站数/2 → 物理 2L−1（左列）；逻辑 L &gt; 站数/2 → 物理 2(L−站数/2)（右列）。
/// </summary>
public static class StationMap
{
    /// <summary>逻辑→物理（1..stationCount 内，越界抛 ArgumentOutOfRangeException——内部 bug 防护）</summary>
    public static int PhysicalOf(int logical, int stationCount) =>
        logical is < 1 ? throw new ArgumentOutOfRangeException(nameof(logical), $"逻辑站口 {logical} < 1")
        : logical > stationCount ? throw new ArgumentOutOfRangeException(nameof(logical), $"逻辑站口 {logical} 越界（站数 {stationCount}）")
        : logical <= stationCount / 2 ? logical * 2 - 1 : (logical - stationCount / 2) * 2;

    /// <summary>物理→逻辑（1..stationCount 内，越界抛 ArgumentOutOfRangeException）</summary>
    public static int LogicalOf(int physical, int stationCount) =>
        physical is < 1 ? throw new ArgumentOutOfRangeException(nameof(physical), $"物理站口 {physical} < 1")
        : physical > stationCount ? throw new ArgumentOutOfRangeException(nameof(physical), $"物理站口 {physical} 越界（站数 {stationCount}）")
        : physical % 2 == 1 ? (physical + 1) / 2 : physical / 2 + stationCount / 2;
}
