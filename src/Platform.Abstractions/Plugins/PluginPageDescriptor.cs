// 02 §4.9 插件页声明：导航元数据 + 能力要求（requiresFeatures 随 IFeatureGate 显隐）。
// 只含字符串与集合——Abstractions 不引 WPF；View 创建契约留在客户端壳
//（Platform.Client.Hosting.IPluginPageProvider，net8.0-windows 才能出现 UI 类型）。

namespace Platform.Abstractions.Plugins;

/// <summary>
/// 插件页描述符（4.9 IPluginUiProvider.Page 的数据面）。
/// RequiresFeatures 为空 = 随插件启用即可见；非空 = 全部键 enabled 才进导航树。
/// </summary>
public sealed record PluginPageDescriptor(
    /// <summary>导航路由键，如 "plugins/vision-camera"。</summary>
    string Route,
    /// <summary>导航显示名。</summary>
    string Title,
    /// <summary>资源字典图标键（保留前缀归平台，4.9.1 资源字典冲突规则）。</summary>
    string IconKey,
    /// <summary>权限码，如 "plugin.camera:view"（服务端 API 仍按权限闸，此处供客户端过滤导航）。</summary>
    string PermCode,
    /// <summary>所需能力键清单（feature_flag 键名）。</summary>
    IReadOnlyList<string> RequiresFeatures);
