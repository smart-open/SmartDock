// S15 对方系统 stub（模拟真实 MES 的 REST 接收端，6.10 契约测试用）：
//   /ok     → 200（正常入账）
//   /down   → 503（对方故障：退避重试）
//   /slow   → 等 15s 再 200（超时口径：平台 Suspended）
// 用法：dotnet run --project tools/MesStubServer -- <port>
// 收到的请求打印幂等键（人工核对入账次数，验收 #18）。

using System.Text;

var port = args.Length > 0 ? int.Parse(args[0]) : 5300;
var listener = new System.Net.HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"MES stub listening on {port}");

var received = new HashSet<string>(StringComparer.Ordinal);
var receivedLock = new object();

while (true)
{
    var context = await listener.GetContextAsync();
    var path = context.Request.Url?.AbsolutePath ?? "/";
    var idem = context.Request.Headers["X-Idempotency-Key"] ?? "(none)";
    // 请求留痕（断网演练核对 0 重复入账）：每条请求一行，含时间戳与幂等键
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {path} key={idem}");
    string respond;
    lock (receivedLock)
    {
        // 同键重复推送：返回 duplicate 提示（入账一次，6.4 铁律 2）
        respond = received.Add(idem) ? "received" : "duplicate";
    }

    try
    {
        switch (path)
        {
            case "/ok":
                await RespondAsync(context, 200, $"{respond}:{idem}");
                break;
            case "/down":
                await RespondAsync(context, 503, "stub down");
                break;
            case "/slow":
                await Task.Delay(15000);
                await RespondAsync(context, 200, "slow-ack");
                break;
            default:
                await RespondAsync(context, 404, "unknown path");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"stub error: {ex.Message}");
    }
}

static async Task RespondAsync(System.Net.HttpListenerContext context, int status, string body)
{
    context.Response.StatusCode = status;
    var bytes = Encoding.UTF8.GetBytes(body);
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}
