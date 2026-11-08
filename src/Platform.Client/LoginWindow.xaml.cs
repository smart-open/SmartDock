namespace Platform.Client;

using System.Windows;
using System.Windows.Input;

/// <summary>
/// 登录窗口：成功后打开主窗口并关闭自身（会话存内存，App 关闭即失效）。
/// 登录失败统一提示（不区分账号不存在/口令错/停用/锁定，防账号枚举展示）。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly Services.AuthApiClient _authClient;
    private bool _loggingIn;

    public LoginWindow(string baseAddress)
    {
        InitializeComponent();
        _authClient = new Services.AuthApiClient(baseAddress);
        UserCodeBox.Focus();
    }

    /// <summary>登录成功后的会话（主窗口读角色/权限控制 UI）。</summary>
    public Services.LoginOutcome? Session { get; private set; }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
        => await LoginAsync();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = LoginAsync();
        }
    }

    private async Task LoginAsync()
    {
        if (_loggingIn)
        {
            return;   // 防连点堆叠
        }

        _loggingIn = true;
        LoginButton.IsEnabled = false;
        ErrorText.Text = string.Empty;
        try
        {
            Session = await _authClient.LoginAsync(UserCodeBox.Text.Trim(), PasswordBox.Password);
            if (Session is null)
            {
                ErrorText.Text = "登录失败：工号或口令不正确，或账号被停用/锁定";
                return;
            }

            DialogResult = true;   // App 启动逻辑据此打开主窗口
        }
        catch (Exception ex)
        {
            // 网络/API 不可达要与凭据错误区分（现场排障第一反应不同）
            ErrorText.Text = $"无法连接平台服务：{ex.Message}";
        }
        finally
        {
            _loggingIn = false;
            LoginButton.IsEnabled = true;
        }
    }
}
