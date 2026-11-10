using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Core.Acquisition;

/// <summary>一条写命令的执行结果（含 WriteGuard 各闸的留痕信息）。</summary>
public sealed record WriteGuardResult(
    long CommandId,
    bool Accepted,
    string? RejectReason,
    bool Verified,
    double? ReadBackValue,
    /// <summary>执行阶段失败原因（设备不可达/写失败/回读不符）；null = 执行未报错。调用方据此区分 200/5xx。</summary>
    string? Error = null);

/// <summary>闸 2（互锁）回调：返回 null = 放行；返回字符串 = 拒绝原因。由上层按业务实现。</summary>
public delegate string? InterlockCheck(long deviceId, long pointId, double value);

/// <summary>
/// WriteGuard 四道闸（02 §5.6）：权限 → 互锁 → 先落库 → 回读。
/// 任何一道闸拒绝/失败都必须在 write_command 留痕——「先落意图再执行」是本类的存在理由。
/// </summary>
public sealed class WriteGuard
{
    // 闸 4 回读窗口：写后等驱动轮询刷新快照的最长时间（快照周期通常 1s，取 3 倍余量）
    private static readonly TimeSpan ReadBackTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<long, IDeviceDriver?> _driverResolver;
    private readonly Func<long, PointRef?> _pointResolver;
    private readonly Func<long, double, string, Task<long>> _persistAsync;   // 闸 3：落库，返回 commandId
    private readonly Func<long, string, Task> _markRejectedAsync;    // 拒绝留痕
    private readonly Func<long, Task> _markSentAsync;
    private readonly Func<long, string?, bool, double?, Task> _finishAsync;  // DONE/FAILED + 回读值
    private readonly Func<long, Task<ReadValue?>> _snapshotLookupAsync;
    private readonly InterlockCheck? _interlockCheck;
    private readonly ILogger<WriteGuard> _logger;

    public WriteGuard(
        Func<long, IDeviceDriver?> driverResolver,
        Func<long, PointRef?> pointResolver,
        Func<long, double, string, Task<long>> persistAsync,
        Func<long, string, Task> markRejectedAsync,
        Func<long, Task> markSentAsync,
        Func<long, string?, bool, double?, Task> finishAsync,
        Func<long, Task<ReadValue?>> snapshotLookupAsync,
        ILogger<WriteGuard> logger,
        InterlockCheck? interlockCheck = null)
    {
        _driverResolver = driverResolver;
        _pointResolver = pointResolver;
        _persistAsync = persistAsync;
        _markRejectedAsync = markRejectedAsync;
        _markSentAsync = markSentAsync;
        _finishAsync = finishAsync;
        _snapshotLookupAsync = snapshotLookupAsync;
        _interlockCheck = interlockCheck;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次受保护的写。注意闸 1（权限）在 API 层做（操作者身份在那里才有），
    /// 这里接收已鉴权的 operator，并把权限留痕写进第一道记录。
    /// </summary>
    public async Task<WriteGuardResult> ExecuteAsync(string operatorName, long pointId, double value,
        CancellationToken cancellationToken = default)
    {
        // —— 闸 2：互锁（业务规则上层注入；无注入则放行）——
        var point = _pointResolver(pointId);
        if (point is null)
        {
            // 未知点位不落库：write_command.point_id 有 device_point 外键，
            // 落 REJECTED 痕必然违反 FK 变裸 500（拒绝留痕只对真实点位成立）
            _logger.LogWarning("write rejected: point {PointId} = {Value} by {Operator}: unknown point",
                pointId, value, operatorName);
            return new WriteGuardResult(CommandId: 0, Accepted: false,
                RejectReason: $"unknown point {pointId}", Verified: false, ReadBackValue: null);
        }

        if (_interlockCheck is not null)
        {
            var interlockReject = _interlockCheck(point.Value.DeviceId, pointId, value);
            if (interlockReject is not null)
            {
                return await RejectAsync(0, operatorName, pointId, value, $"interlock: {interlockReject}");
            }
        }

        // —— 闸 3：先落库再执行（PENDING）——
        var commandId = await _persistAsync(pointId, value, operatorName);

        // 执行段整体兜底：连接/写/回读任何一步抛异常（驱动 NRE、宿主重启窗口、停机取消、
        // 落库函数本身故障）都不能让命令卡在 PENDING/SENT——必须写终态，否则审计链断裂
        try
        {
            return await ExecuteCoreAsync(commandId, pointId, point.Value, value, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 取消（客户端断开/停机）：命令已留痕，写 FAILED 终态再按取消语义上抛
            await SafeFinishAsync(commandId, "write aborted: cancelled during execution");
            throw;
        }
        catch (Exception ex)
        {
            await SafeFinishAsync(commandId, $"write aborted: {ex.Message}");
            throw;
        }
    }

    private async Task<WriteGuardResult> ExecuteCoreAsync(long commandId, long pointId, PointRef point, double value,
        CancellationToken cancellationToken)
    {

        // —— 执行：找到该设备的驱动实例（调度器持有，按 deviceId 解析）——
        if (_driverResolver(point.DeviceId) is not IDeviceWriteDriver writeDriver)
        {
            return await FailAsync(commandId, "device driver does not support write (or device offline)");
        }

        // 确保已连接（写路径自己兜底连接；采集循环的连接状态由驱动幂等 Connect 保证）
        var connect = await writeDriver.ConnectAsync(cancellationToken);
        if (!connect.Success)
        {
            return await FailAsync(commandId, $"connect failed: {connect.Error}");
        }

        var writeResult = await writeDriver.WriteAsync(point, value, cancellationToken);
        if (!writeResult.Success)
        {
            return await FailAsync(commandId, $"write failed: {writeResult.Error}");
        }

        await _markSentAsync(commandId);

        // —— 闸 4：回读校验（等快照刷新后比对）——
        var verified = false;
        double? readBack = null;
        var deadline = DateTime.UtcNow + ReadBackTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, cancellationToken);
            var snapshot = await _snapshotLookupAsync(pointId);
            if (snapshot is { Quality: QualityCode.Good } readValue)
            {
                readBack = readValue.Value;
                // Float32 精度：写入值经 float 往返，用相对容差比较
                verified = readValue.Value is not null &&
                           Math.Abs(readValue.Value.Value - value) <= Math.Abs(value) * 1e-5 + 1e-3;
                // 首读可能还是写入前的旧值（采集周期 1s > 轮询间隔 300ms）：
                // 不匹配就继续等下一轮快照，直到超时；匹配立即通过
                if (verified)
                {
                    break;
                }
            }
        }

        await _finishAsync(commandId, verified ? null : "read-back mismatch or timeout", verified, readBack);
        _logger.LogInformation(
            "write command {CommandId} point {PointId} = {Value}: device write ok, verified={Verified}, readBack={ReadBack}",
            commandId, pointId, value, verified, readBack);

        return new WriteGuardResult(commandId, Accepted: true, RejectReason: null, verified, readBack);
    }

    /// <summary>终态落库失败只记日志：命令卡中间态总好过把终态异常再抛给调用方掩盖原始异常。</summary>
    private async Task SafeFinishAsync(long commandId, string error)
    {
        try
        {
            await _finishAsync(commandId, error, false, null);
            _logger.LogWarning("write command {CommandId} aborted: {Error}", commandId, error);
        }
        catch (Exception finishEx)
        {
            _logger.LogError(finishEx, "write command {CommandId}: failed to record FAILED state", commandId);
        }
    }

    private async Task<WriteGuardResult> RejectAsync(long commandId, string operatorName, long pointId,
        double value, string reason)
    {
        // 拒绝也必须落库留痕（REJECTED），否则审计链断裂
        var id = commandId != 0 ? commandId : await _persistAsync(pointId, value, operatorName);
        await _markRejectedAsync(id, reason);
        _logger.LogWarning("write rejected: point {PointId} = {Value} by {Operator}: {Reason}",
            pointId, value, operatorName, reason);
        return new WriteGuardResult(id, Accepted: false, reason, Verified: false, ReadBackValue: null);
    }

    private async Task<WriteGuardResult> FailAsync(long commandId, string error)
    {
        await _finishAsync(commandId, error, false, null);
        _logger.LogWarning("write command {CommandId} failed: {Error}", commandId, error);
        // Accepted=true 仅表示「通过了闸 1-3」，执行失败必须把 error 带出去，
        // 否则 API 层会把设备写失败当成 200 成功返回（客户端误以为写成功）
        return new WriteGuardResult(commandId, Accepted: true, RejectReason: null, Verified: false,
            ReadBackValue: null, Error: error);
    }
}
