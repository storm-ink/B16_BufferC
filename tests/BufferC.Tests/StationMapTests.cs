using BufferC.Core.Core;
using Xunit;

namespace BufferC.Tests;

/// <summary>站口物理↔逻辑映射（2026-08-20 现场口径）：蛇形布线——左列奇槽/右列偶槽，逻辑编号按列自上而下</summary>
public class StationMapTests
{
    [Fact]
    public void SixteenStations_FullTable_ForwardAndBackward()
    {
        // 16 站：物理 1-16 ↔ 逻辑 1,9,2,10,3,11,4,12,5,13,6,14,7,15,8,16
        int[] logicalOf = { 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15, 8, 16 };
        for (int p = 1; p <= 16; p++)
        {
            Assert.Equal(logicalOf[p - 1], StationMap.LogicalOf(p, 16));
            Assert.Equal(p, StationMap.PhysicalOf(logicalOf[p - 1], 16));   // 反向回到原物理
        }
        // 边界：逻辑 1→物理1、逻辑 8→物理15、逻辑 9→物理2、逻辑 16→物理16
        Assert.Equal(1, StationMap.PhysicalOf(1, 16));
        Assert.Equal(15, StationMap.PhysicalOf(8, 16));
        Assert.Equal(2, StationMap.PhysicalOf(9, 16));
        Assert.Equal(16, StationMap.PhysicalOf(16, 16));
    }

    [Fact]
    public void EightStations_FullTable_ForwardAndBackward()
    {
        // 8 站：物理 1-8 ↔ 逻辑 1,5,2,6,3,7,4,8
        int[] logicalOf = { 1, 5, 2, 6, 3, 7, 4, 8 };
        for (int p = 1; p <= 8; p++)
        {
            Assert.Equal(logicalOf[p - 1], StationMap.LogicalOf(p, 8));
            Assert.Equal(p, StationMap.PhysicalOf(logicalOf[p - 1], 8));
        }
        // 边界：逻辑 1→物理1、逻辑 4→物理7、逻辑 5→物理2、逻辑 8→物理8
        Assert.Equal(1, StationMap.PhysicalOf(1, 8));
        Assert.Equal(7, StationMap.PhysicalOf(4, 8));
        Assert.Equal(2, StationMap.PhysicalOf(5, 8));
        Assert.Equal(8, StationMap.PhysicalOf(8, 8));
    }

    [Fact]
    public void OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StationMap.PhysicalOf(0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => StationMap.PhysicalOf(17, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => StationMap.LogicalOf(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => StationMap.LogicalOf(9, 8));   // 8 站机台物理 9 无逻辑对应（幻影防护）
    }
}
