using ToolDock.Core;

namespace ToolDock.Tests;

/// <summary>清理核心的掩码契约与非提权兜底路径（不做需要管理员的实际清理）。</summary>
public class CleanupCoreTests
{
    [Fact]
    public void MaskDefault_MatchesDocumentedBits()
    {
        Assert.Equal(0xE7UL, CleanupCore.MaskDefault);
        Assert.Equal(
            CleanupCore.MaskDefault,
            CleanupCore.RegionWorkingSet | CleanupCore.RegionSystemFileCache | CleanupCore.RegionStandbyPriority0 |
            CleanupCore.RegionRegistryCache | CleanupCore.RegionCombineMemoryLists | CleanupCore.RegionModifiedFileCache);

        // 冻结区（备用列表 + 已修改列表）不在默认掩码里
        Assert.Equal(0UL, CleanupCore.MaskDefault & (CleanupCore.RegionStandbyList | CleanupCore.RegionModifiedList));
    }

    [Fact]
    public void TrimWorkingSets_WithoutWorkingSetBit_ReportsNoWork()
    {
        var result = CleanupCore.TrimWorkingSets(0);

        Assert.Single(result.Regions);
        Assert.Equal(0xC0000022u, result.Regions[0].Status);
        Assert.Contains("trimmed=0", result.PrivilegeDiag);
    }

    [Fact]
    public void TrimWorkingSets_WithWorkingSet_ReportsRegionAndCounts()
    {
        var result = CleanupCore.TrimWorkingSets(CleanupCore.RegionWorkingSet);

        Assert.Single(result.Regions);
        Assert.Contains("逐进程", result.Regions[0].Name);
        Assert.Contains("trimmed=", result.PrivilegeDiag);
    }

    [Fact]
    public void ReadMemorySnapshot_ReturnsPlausibleValues()
    {
        var snapshot = CleanupCore.ReadMemorySnapshot();

        Assert.True(snapshot.TotalBytes > 0);
        Assert.True(snapshot.LoadPercent <= 100);
        Assert.True(snapshot.FreeBytes <= snapshot.TotalBytes);
    }
}
