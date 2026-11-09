// 02 §4.9.1 插件页宿主规则（客户端崩溃隔离）：一页崩，只禁一页，主窗口和其他页照常可用。
// 隔离手段：CreateView 与首绑在 try/catch 内；异常 → 错误面板 + 本会话禁再进（不做热恢复）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Platform.Client.Hosting;

/// <summary>插件页提供者契约（客户端侧最小面；完整 IPluginUiProvider 随插件 UI 交付扩展）。</summary>
public interface IPluginPageProvider
{
    /// <summary>导航显示名。</summary>
    string Title { get; }

    /// <summary>在 UI 线程创建页面内容；实现方禁止阻塞 IO（4.9.1 插件侧义务）。</summary>
    UIElement CreateView();
}

/// <summary>委托式提供者：平台内置页与演示页用 lambda 挂进宿主，免为每页建类。</summary>
public sealed class DelegatePageProvider(string title, Func<UIElement> createView) : IPluginPageProvider
{
    public string Title => title;

    public UIElement CreateView() => createView();
}

public enum PluginPageState
{
    Ready,

    /// <summary>本会话禁用：崩溃后不再实例化（4.9.1 崩溃后降级；重启客户端恢复）。</summary>
    Disabled,
}

public sealed class PluginPageHost : ContentControl
{
    private static readonly Dictionary<string, PluginPageState> SessionStates = [];
    private static readonly object StateLock = new();

    private readonly string _pageKey;

    public PluginPageHost(string pageKey)
    {
        _pageKey = pageKey;
    }

    /// <summary>会话级状态（静态共享：同页在导航间切换仍记住 Disabled）。</summary>
    public static PluginPageState GetSessionState(string pageKey)
    {
        lock (StateLock)
        {
            return SessionStates.TryGetValue(pageKey, out var state) ? state : PluginPageState.Ready;
        }
    }

    public void Activate(IPluginPageProvider provider)
    {
        lock (StateLock)
        {
            if (SessionStates.TryGetValue(_pageKey, out var state) && state == PluginPageState.Disabled)
            {
                Content = BuildErrorView(provider.Title,
                    new InvalidOperationException("该页已在本会话禁用（先前崩溃），重启客户端恢复"));
                return;
            }
        }

        try
        {
            // 只保护实例化与首次布局（4.9.1 节选口径）；异步加载的异常归插件自身处理
            var view = provider.CreateView();
            Content = view;
        }
        catch (Exception ex)
        {
            // plugin_ui.crash：M3 阶段落诊断日志（TraceId 随完整审计接入）；本会话禁再进
            System.Diagnostics.Trace.WriteLine($"plugin_ui.crash page={_pageKey} {ex.GetType().Name}: {ex.Message}");
            lock (StateLock)
            {
                SessionStates[_pageKey] = PluginPageState.Disabled;
            }
            Content = BuildErrorView(provider.Title, ex);
        }
    }

    /// <summary>错误面板：不重抛——冒到 DispatcherUnhandledException 就是整客户端退出（4.9.1 违反后果）。</summary>
    private static UIElement BuildErrorView(string title, Exception error)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"页面「{title}」加载失败",
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            Foreground = Brushes.IndianRed,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{error.GetType().Name}: {error.Message}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.DimGray,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "该页已禁用，其他页面不受影响；重启客户端可恢复。",
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }
}
