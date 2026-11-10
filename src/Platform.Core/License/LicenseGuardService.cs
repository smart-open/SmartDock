// 02/03 §13.6.3 超限软限与降级写闸：超限是软限——存量不追溯（已有设备/用户继续运行），
// 只拒绝「新增」；到期 Degraded/Expired 阶段关新增类写（新增设备/用户/过账），
// 采集、报警、审计永不受影响（13.6.3 底线）。闸集中在本类，端点只调 Check。

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Core.License;

/// <summary>闸判定结果：Allowed=false 时 RejectReason 供端点回 403 + 审计留痕。</summary>
public sealed record LicenseGuardResult(bool Allowed, string? RejectReason)
{
    public static LicenseGuardResult Pass() => new(true, null);
    public static LicenseGuardResult Reject(string reason) => new(false, reason);
}

public sealed class LicenseGuardService(
    FileLicenseService license,
    Func<int> currentDeviceCount,
    Func<int> currentUserCount,
    ILogger<LicenseGuardService>? logger = null)
{
    private readonly ILogger<LicenseGuardService> _logger =
        logger ?? NullLogger<LicenseGuardService>.Instance;

    /// <summary>新增设备闸：超限拒绝（13.6.2 limits.devices）；Degraded/Expired 一并拒绝。</summary>
    public LicenseGuardResult CheckDeviceCreation()
    {
        var phaseGate = CheckWritePhase("新增设备");
        if (!phaseGate.Allowed)
        {
            return phaseGate;
        }

        var result = license.Current();
        if (result.Loaded && result.Document is { } doc)
        {
            // 存量不追溯：只在新增时刻对比当前数量与上限
            if (currentDeviceCount() >= doc.Limits.Devices)
            {
                _logger.LogWarning("device limit reached ({Limit}), creation rejected", doc.Limits.Devices);
                return LicenseGuardResult.Reject($"DEVICE_LIMIT: 设备数已达授权上限（{doc.Limits.Devices}），请升级授权");
            }
        }
        return LicenseGuardResult.Pass();
    }

    /// <summary>新增用户闸：同口径（limits.users）。</summary>
    public LicenseGuardResult CheckUserCreation()
    {
        var phaseGate = CheckWritePhase("新增用户");
        if (!phaseGate.Allowed)
        {
            return phaseGate;
        }

        var result = license.Current();
        if (result.Loaded && result.Document is { } doc)
        {
            if (currentUserCount() >= doc.Limits.Users)
            {
                _logger.LogWarning("user limit reached ({Limit}), creation rejected", doc.Limits.Users);
                return LicenseGuardResult.Reject($"USER_LIMIT: 用户数已达授权上限（{doc.Limits.Users}），请升级授权");
            }
        }
        return LicenseGuardResult.Pass();
    }

    /// <summary>13.6.3 降级写闸：Degraded（过期 ≤90 天）与 Expired 关新增类写；Grace 只提醒。</summary>
    private LicenseGuardResult CheckWritePhase(string operation)
    {
        return license.Phase switch
        {
            LicensePhase.Degraded => LicenseGuardResult.Reject(
                $"LICENSE_DEGRADED: 授权已过期（降级期），{operation}不可用；采集与报警不受影响，请续期"),
            LicensePhase.Expired => LicenseGuardResult.Reject(
                $"LICENSE_EXPIRED: 授权已过期超过降级期，{operation}不可用；采集与报警不受影响，请续期"),
            _ => LicenseGuardResult.Pass(),
        };
    }
}
