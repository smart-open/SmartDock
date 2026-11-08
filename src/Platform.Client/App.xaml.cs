namespace Platform.Client;

using System.Windows;

/// <summary>
/// 启动流程：登录窗口（模态）→ 成功才进主窗口（M1-9 三页壳入口）。
/// 会话存客户端内存，App 关闭即失效；写操作的服务端闸门是 WriteGuard（5.6）。
/// </summary>
public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var baseAddress = ReadBaseAddress();

        var login = new LoginWindow(baseAddress);
        if (login.ShowDialog() != true || login.Session is null)
        {
            Shutdown();   // 用户关闭登录框 = 退出程序
            return;
        }

        var main = new MainWindow(baseAddress, login.Session);
        MainWindow = main;
        main.Show();
    }

    private static string ReadBaseAddress()
    {
        // WPF 无默认配置系统：直接读 exe 旁的 appsettings.json（M0 约定延续）
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            return json.RootElement.GetProperty("Api").GetProperty("BaseAddress").GetString()
                ?? "http://localhost:5000";
        }
        catch
        {
            return "http://localhost:5000";
        }
    }
}
