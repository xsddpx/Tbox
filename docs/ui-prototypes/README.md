# Tbox PowerToys 风格界面原型

该目录只保留一套可映射到 WinUI 3 控件结构的候选方案，包含主页与常规设置页。

## 设计约束

- 搜索框位于应用标题栏中。
- 桌面宽度使用固定侧栏；中等和窄屏改为汉堡按钮控制的浮层侧栏。
- 侧栏导航区可滚动，欢迎、新增内容和反馈入口固定在底部并始终可见。
- 侧栏只展示主页、常规和当前已有工具，不提供“全部工具”入口。
- 常规页参照 PowerToys 使用语言和主题下拉框，不提供强调色设置。
- 常规页不展示应用模式模块；全窗口统一使用页面标题、分区/卡片标题、正文/控件、辅助说明四级字号。
- 窄屏下主页改为单列，设置行的说明和控件上下排列。
- 支持浅色、深色和跟随 Windows；主题仅从常规设置页修改。

## 使用

推荐在仓库根目录运行下面的命令，再打开脚本输出的 HTTP 地址；这样可避免部分应用内浏览器无法加载带查询参数的本地 HTML 文件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\docs\ui-prototypes\serve-prototype.ps1
```

也可以直接用普通浏览器打开 `index.html`。点击主页或常规导航可切换页面；缩放浏览器窗口可查看固定侧栏、浮层侧栏和窄屏单列布局。

## 生成预览图

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\docs\ui-prototypes\render-screenshots.ps1
```

脚本生成主页/常规的日夜对比图，以及常规页的中等宽度侧栏浮层和窄屏预览图。
