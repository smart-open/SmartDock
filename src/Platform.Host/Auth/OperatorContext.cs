// M1 会话口径的操作人身份解析：X-Operator 头承载用户 id（Alarm/Feature/Identity 端点同源）。
// 完整 JWT 后换 Claims 通道，调用方契约不变（ParseOperator 一处换实现）。

namespace Platform.Host.Auth;

public static class OperatorContext
{
    public static long? ParseOperator(HttpContext context)
    {
        var raw = context.Request.Headers["X-Operator"].FirstOrDefault();
        return long.TryParse(raw, out var id) ? id : null;
    }
}
