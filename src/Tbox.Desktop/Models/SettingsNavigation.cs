namespace Tbox.Desktop.Models;

internal enum SettingsPage { Home, General, Folders, Capture }

internal static class SettingsNavigation
{
    public static SettingsPage Search(string? query)
    {
        var text = (query ?? "").Trim();
        if (Contains("截图", "贴图", "标注", "录屏", "ocr", "识别", "缓存", "capture")) return SettingsPage.Capture;
        if (Contains("文件", "目录", "finder", "folder", "跳转")) return SettingsPage.Folders;
        if (Contains("主题", "外观", "启动", "通知", "快捷键", "更新", "版本", "常规", "theme", "startup")) return SettingsPage.General;
        return SettingsPage.Home;
        bool Contains(params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
