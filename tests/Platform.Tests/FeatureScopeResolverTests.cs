// 3.7.1 范围解析单测：FeatureScopeResolver 是 FeatureStore.Resolve 缓存命中后的
// 内存执行体，必须与原 SQL 口径逐条对齐（层级优先序 / 生效窗口 / 同层取新 / 隔离性）。

using Platform.Core.Features;
using Platform.Abstractions.Features;

namespace Platform.Tests;

public class FeatureScopeResolverTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static FeatureScopeOverrideRow Row(
        string key = "f1", string scopeType = "global", string scopeId = "*",
        bool enabled = true, long id = 1, DateTime? from = null, DateTime? to = null) =>
        new(id, key, scopeType, scopeId, enabled, from, to);

    private static FeatureScope Scope(string? line = null, string? workshop = null, string factory = "F1") =>
        new(factory, workshop, line);

    [Fact]
    public void LineBeatsWorkshopFactoryGlobal()
    {
        var rows = new[]
        {
            Row(scopeType: "global", enabled: false),
            Row(scopeType: "factory", scopeId: "F1", enabled: false),
            Row(scopeType: "workshop", scopeId: "W1", enabled: false),
            Row(scopeType: "line", scopeId: "L1", enabled: true),
        };

        var resolved = FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1", workshop: "W1"), Now);

        Assert.True(resolved);
    }

    [Fact]
    public void WorkshopBeatsFactoryGlobal()
    {
        var rows = new[]
        {
            Row(scopeType: "global", enabled: false),
            Row(scopeType: "factory", scopeId: "F1", enabled: true),
            Row(scopeType: "workshop", scopeId: "W1", enabled: false),
        };

        var resolved = FeatureScopeResolver.Resolve(rows, "f1", Scope(workshop: "W1"), Now);

        Assert.False(resolved);
    }

    [Fact]
    public void LineOverrideDoesNotLeakToOtherLines()
    {
        // 隔离性：L1 的 line 覆盖对 L2 无效，L2 只能看到 factory 层覆盖
        var rows = new[]
        {
            Row(scopeType: "factory", scopeId: "F1", enabled: false),
            Row(scopeType: "line", scopeId: "L1", enabled: true),
        };

        Assert.True(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1"), Now));
        Assert.False(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L2"), Now));
    }

    [Fact]
    public void NullScopeOnlyMatchesGlobal()
    {
        var rows = new[]
        {
            Row(scopeType: "factory", scopeId: "F1", enabled: true),
            Row(scopeType: "global", enabled: false),
        };

        Assert.False(FeatureScopeResolver.Resolve(rows, "f1", null, Now));
    }

    [Fact]
    public void NoMatchingRowReturnsNull()
    {
        var rows = new[]
        {
            Row(scopeType: "line", scopeId: "L1", enabled: true),
        };

        // 请求范围不在任何覆盖层内 = 无覆盖（调用方回落 default_enabled）
        Assert.Null(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L9"), Now));
    }

    [Fact]
    public void UnknownScopeTypeRowIsIgnored()
        // 防御脏数据：未知 scope_type 不参与匹配（与原 SQL 行为一致）
        => Assert.Null(FeatureScopeResolver.Resolve(
            [Row(scopeType: "cell", scopeId: "C1", enabled: true)], "f1", Scope(), Now));

    [Fact]
    public void NotYetEffectiveRowIsIgnored()
    {
        var rows = new[]
        {
            Row(scopeType: "global", enabled: false),
            Row(scopeType: "line", scopeId: "L1", enabled: true, from: Now.AddMinutes(1)),
        };

        Assert.False(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1"), Now));
    }

    [Fact]
    public void ExpiredRowIsIgnored()
    {
        var rows = new[]
        {
            Row(scopeType: "global", enabled: false),
            Row(scopeType: "line", scopeId: "L1", enabled: true, to: Now.AddSeconds(-1)),
        };

        Assert.False(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1"), Now));
    }

    [Fact]
    public void BoundaryInclusiveFromExclusiveTo()
    {
        // effective_from <= now 生效（含端点）；effective_to > now 才生效（开区间）
        var fromBoundary = new[]
        {
            Row(scopeType: "global", enabled: false),
            Row(scopeType: "line", scopeId: "L1", enabled: true, from: Now),
        };
        Assert.True(FeatureScopeResolver.Resolve(fromBoundary, "f1", Scope(line: "L1"), Now));

        var toBoundary = new[]
        {
            Row(scopeType: "global", enabled: false),
            Row(scopeType: "line", scopeId: "L1", enabled: true, to: Now),
        };
        Assert.False(FeatureScopeResolver.Resolve(toBoundary, "f1", Scope(line: "L1"), Now));
    }

    [Fact]
    public void SameRankLatestEffectiveFromWins()
    {
        var rows = new[]
        {
            Row(scopeType: "line", scopeId: "L1", enabled: false, id: 1, from: Now.AddHours(-2)),
            Row(scopeType: "line", scopeId: "L1", enabled: true, id: 2, from: Now.AddHours(-1)),
        };

        Assert.True(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1"), Now));
    }

    [Fact]
    public void SameRankNullEffectiveFromLosesToDated()
    {
        // NULLS LAST：无 effective_from 的行在同层排最后
        var rows = new[]
        {
            Row(scopeType: "line", scopeId: "L1", enabled: false, id: 1, from: null),
            Row(scopeType: "line", scopeId: "L1", enabled: true, id: 2, from: Now.AddHours(-5)),
        };

        Assert.True(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1"), Now));
    }

    [Fact]
    public void SameRankSameFromHigherIdWins()
    {
        var rows = new[]
        {
            Row(scopeType: "line", scopeId: "L1", enabled: false, id: 7, from: Now.AddHours(-1)),
            Row(scopeType: "line", scopeId: "L1", enabled: true, id: 9, from: Now.AddHours(-1)),
        };

        Assert.True(FeatureScopeResolver.Resolve(rows, "f1", Scope(line: "L1"), Now));
    }

    [Fact]
    public void OtherKeyRowsAreIgnored()
    {
        var rows = new[]
        {
            Row(key: "f2", scopeType: "global", enabled: true),
        };

        Assert.Null(FeatureScopeResolver.Resolve(rows, "f1", null, Now));
    }
}
