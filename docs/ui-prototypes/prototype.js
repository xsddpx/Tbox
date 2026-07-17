const iconPaths = {
  menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  home: '<path d="M3 11.5 12 4l9 7.5"/><path d="M5.5 10.5V20h13v-9.5"/><path d="M9.5 20v-6h5v6"/>',
  folder: '<path d="M3 6.5h6l2 2h10v10.5H3z"/><path d="M3 8.5V5h7l2 2"/>',
  crop: '<path d="M7 3v14a2 2 0 0 0 2 2h12"/><path d="M3 7h14a2 2 0 0 1 2 2v12"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6a1.7 1.7 0 0 0 1-1.6v-.2h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z"/>',
  grid: '<rect x="4" y="4" width="6" height="6" rx="1"/><rect x="14" y="4" width="6" height="6" rx="1"/><rect x="4" y="14" width="6" height="6" rx="1"/><rect x="14" y="14" width="6" height="6" rx="1"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
  check: '<path d="m5 12 4 4L19 6"/>',
  chevron: '<path d="m9 18 6-6-6-6"/>',
  palette: '<path d="M12 3a9 9 0 1 0 0 18h1.4a1.7 1.7 0 0 0 1.2-2.9l-.3-.3a1.7 1.7 0 0 1 1.2-2.9H17a4 4 0 0 0 4-4C21 6.5 17 3 12 3Z"/>',
  power: '<path d="M12 2v10"/><path d="M6.5 5.5a8 8 0 1 0 11 0"/>',
  clipboard: '<rect x="5" y="5" width="14" height="16" rx="2"/><path d="M9 5V3h6v2M9 10h6M9 14h6"/>',
  language: '<path d="M4 5h10M9 3v2M6 5c.4 4 3 7 7 8M12 5c-.5 4-3 7-7 8"/><path d="m14 20 3-8 3 8M15 17h4"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7h.01"/>',
  feedback: '<path d="M4 5h16v11H9l-5 4z"/>',
  news: '<path d="M4 5h16v14H4z"/><path d="M8 9h8M8 13h5"/>'
};

const root = document.getElementById("prototypeRoot");
const toast = document.getElementById("prototypeToast");
const query = new URLSearchParams(window.location.search);
const state = {
  page: query.get("page") === "general" ? "general" : "home",
  theme: localStorage.getItem("tbox-prototype-theme") || "system",
  compare: query.get("compare") === "1",
  sidebarOpen: query.get("sidebar") === "1"
};
let toastTimer;

function icon(name) {
  return `<span class="inline-icon" aria-hidden="true"><svg viewBox="0 0 24 24">${iconPaths[name] || iconPaths.grid}</svg></span>`;
}

function toggle(label, enabled = true) {
  return `<button type="button" class="switch-control${enabled ? " is-on" : ""}" role="switch" aria-checked="${enabled}" aria-label="${label}"></button>`;
}

function keycap(text) {
  return `<span class="keycap">${text}</span>`;
}

function resolveTheme(theme) {
  if (theme !== "system") return theme;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function titlebar() {
  return `
    <header class="window-titlebar">
      <div class="title-start">
        <button type="button" class="menu-button" data-action="toggle-sidebar" aria-label="打开导航">${icon("menu")}</button>
        <span class="app-logo">T</span>
        <strong>Tbox 设置</strong>
      </div>
      <label class="title-search">${icon("search")}<input type="search" placeholder="搜索设置" aria-label="搜索设置"></label>
      <div class="window-controls" aria-hidden="true"><span>—</span><span>□</span><span>×</span></div>
    </header>`;
}

function navRow(iconName, label, page = "", badge = "") {
  const active = page && state.page === page;
  const pageAttribute = page ? ` data-page="${page}"` : ` data-action-name="打开${label}"`;
  return `<button type="button" class="power-nav-row${active ? " is-active" : ""}"${pageAttribute}>${icon(iconName)}<span>${label}</span>${badge ? `<em>${badge}</em>` : ""}</button>`;
}

function sidebar() {
  return `
    <aside class="power-sidebar" aria-label="Tbox 导航">
      <div class="power-sidebar-scroll">
        ${navRow("home", "主页", "home")}
        ${navRow("settings", "常规", "general")}
        <div class="power-nav-divider"></div>
        <div class="power-nav-label">实用工具</div>
        ${navRow("folder", "文件夹跳转")}
        ${navRow("crop", "截图与贴图", "", "新")}
      </div>
      <footer class="power-sidebar-bottom">
        <button type="button" data-action-name="欢迎使用 Tbox">${icon("info")}<span>欢迎使用 Tbox</span></button>
        <button type="button" data-action-name="查看新增内容">${icon("news")}<span>新增内容</span></button>
        <button type="button" data-action-name="提供反馈">${icon("feedback")}<span>提供反馈</span></button>
      </footer>
    </aside>
    <button type="button" class="sidebar-backdrop" data-action="close-sidebar" aria-label="关闭导航"></button>`;
}

function pageSignals() {
  return `
    <div class="page-signals">
      <div>${icon("grid")}<span><strong>快捷键冲突</strong><small>未找到冲突</small></span></div>
      <div>${icon("check")}<span><strong>当前版本</strong><small>1.0.13</small></span></div>
    </div>`;
}

function toolRow(iconName, title, description, enabled = true) {
  return `
    <div class="tool-row">
      <span class="tool-icon">${icon(iconName)}</span>
      <div><strong>${title}</strong><small>${description}</small></div>
      ${toggle(`启用${title}`, enabled)}
      <button type="button" class="chevron-button" data-action-name="打开${title}设置" aria-label="打开${title}设置">${icon("chevron")}</button>
    </div>`;
}

function homePage() {
  return `
    <div class="page-scroll">
      <header class="page-header"><div><h1>主页</h1><p>高频操作和工具状态集中在一个页面</p></div>${pageSignals()}</header>
      <section class="home-grid">
        <div class="home-left">
          <article class="content-card quick-card">
            <h2>快速访问</h2>
            <div class="quick-actions">
              <button type="button" data-action-name="开始截图"><span class="tool-icon">${icon("crop")}</span><strong>开始截图</strong><small>F6</small></button>
              <button type="button" data-action-name="剪贴板贴图"><span class="tool-icon">${icon("clipboard")}</span><strong>剪贴板贴图</strong><small>F7</small></button>
              <button type="button" data-action-name="跳转文件夹"><span class="tool-icon">${icon("folder")}</span><strong>跳转文件夹</strong><small>Ctrl + G</small></button>
            </div>
          </article>
          <article class="content-card shortcuts-card">
            <h2>快捷键</h2>
            <div><span>开始截图</span>${keycap("F6")}</div>
            <div><span>剪贴板贴图</span>${keycap("F7")}</div>
            <div><span>显示 / 隐藏全部贴图</span>${keycap("F8")}</div>
            <div><span>跳转到最近目录</span>${keycap("Ctrl + G")}</div>
          </article>
        </div>
        <article class="content-card utilities-card">
          <header><h2>实用程序</h2><span>2 个已启用</span></header>
          ${toolRow("folder", "文件夹跳转", "快速定位最近目录")}
          ${toolRow("crop", "截图与贴图", "截图、标注并固定图像")}
        </article>
      </section>
    </div>`;
}

function themeSelect(theme) {
  return `
    <select class="setting-select" data-theme-select aria-label="主题">
      <option value="system"${theme === "system" ? " selected" : ""}>Windows 默认</option>
      <option value="light"${theme === "light" ? " selected" : ""}>浅色</option>
      <option value="dark"${theme === "dark" ? " selected" : ""}>深色</option>
    </select>`;
}

function settingRow(iconName, title, description, control) {
  return `<div class="setting-row"><div class="setting-copy">${icon(iconName)}<span><strong>${title}</strong><small>${description}</small></span></div>${control}</div>`;
}

function generalPage(theme) {
  return `
    <div class="page-scroll settings-page">
      <header class="page-header"><div><h1>常规</h1><p>管理 Tbox 的更新、外观和应用行为</p></div></header>
      <h2 class="section-heading">版本和更新</h2>
      <section class="settings-card">
        ${settingRow("check", "Tbox 1.0.13", "上次检查：刚刚", '<button type="button" class="secondary-button" data-action-name="检查更新">检查更新</button>')}
      </section>
      <section class="update-banner">
        ${icon("check")}<div><strong>已安装最新版本</strong><small>Tbox 会定期检查可用更新</small></div><button type="button" data-action-name="查看新增功能">查看新增功能</button>
      </section>
      <h2 class="section-heading">外观和行为</h2>
      <section class="settings-card">
        ${settingRow("language", "语言", "默认情况下，Tbox 与 Windows 语言匹配", '<select class="setting-select" aria-label="语言"><option>Windows 默认</option><option>简体中文</option><option>English</option></select>')}
        ${settingRow("palette", "主题", "跟随 Windows，或单独选择浅色和深色", themeSelect(theme))}
        ${settingRow("power", "开机自启动", "登录 Windows 后自动运行 Tbox", `<span class="setting-toggle-label">开 ${toggle("启用开机自启动")}</span>`)}
        ${settingRow("grid", "显示系统托盘图标", "从托盘快速打开主页和常用工具", `<span class="setting-toggle-label">开 ${toggle("显示系统托盘图标")}</span>`)}
        ${settingRow("feedback", "快速访问浮出控件", "快速访问模块和工具切换", `<span class="setting-toggle-label">开 ${toggle("启用快速访问浮出控件")}</span>`)}
      </section>
      <h2 class="section-heading">快捷键</h2>
      <section class="settings-card">
        ${settingRow("grid", "快捷键冲突检测", "当前未发现与其他应用冲突的快捷方式", '<button type="button" class="secondary-button" data-action-name="重新检测快捷键">重新检测</button>')}
        ${settingRow("clipboard", "激活快捷键", "打开 Tbox 主页并快速启动工具", '<button type="button" class="secondary-button" data-action-name="分配激活快捷键">＋ 分配快捷键</button>')}
      </section>
    </div>`;
}

function renderApp(theme) {
  return `
    <article class="app-window${state.sidebarOpen ? " is-sidebar-open" : ""}" data-theme="${resolveTheme(theme)}">
      ${titlebar()}
      <div class="app-body">
        ${sidebar()}
        <main class="page-content">${state.page === "general" ? generalPage(theme) : homePage()}</main>
      </div>
    </article>`;
}

function render() {
  if (state.compare) {
    document.body.classList.add("compare-mode");
    root.innerHTML = `
      <section class="comparison-page">
        <header class="comparison-heading"><h1>${state.page === "general" ? "常规设置" : "主页"} · 日夜对比</h1><p>唯一候选方案，同一结构仅切换主题</p></header>
        <div class="comparison-grid">
          <section class="comparison-panel"><div class="theme-label">浅色</div>${renderApp("light")}</section>
          <section class="comparison-panel"><div class="theme-label">深色</div>${renderApp("dark")}</section>
        </div>
      </section>`;
    return;
  }
  document.body.classList.remove("compare-mode");
  root.innerHTML = `<section class="interactive-stage">${renderApp(state.theme)}</section>`;
}

function showToast(message) {
  toast.textContent = `已触发：${message}`;
  toast.classList.add("is-visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("is-visible"), 1600);
}

document.addEventListener("click", event => {
  const pageButton = event.target.closest("[data-page]");
  if (pageButton) {
    state.page = pageButton.dataset.page;
    state.sidebarOpen = false;
    const url = new URL(window.location.href);
    url.searchParams.set("page", state.page);
    history.replaceState({}, "", url);
    render();
    return;
  }

  const action = event.target.closest("[data-action]");
  if (action) {
    state.sidebarOpen = action.dataset.action === "toggle-sidebar" ? !state.sidebarOpen : false;
    render();
    return;
  }

  const switchControl = event.target.closest(".switch-control");
  if (switchControl) {
    const enabled = !switchControl.classList.contains("is-on");
    switchControl.classList.toggle("is-on", enabled);
    switchControl.setAttribute("aria-checked", String(enabled));
    showToast(`${switchControl.getAttribute("aria-label")}：${enabled ? "开启" : "关闭"}`);
    return;
  }

  const namedAction = event.target.closest("[data-action-name]");
  if (namedAction) showToast(namedAction.dataset.actionName);
});

document.addEventListener("change", event => {
  if (!event.target.matches("[data-theme-select]")) return;
  state.theme = event.target.value;
  localStorage.setItem("tbox-prototype-theme", state.theme);
  render();
});

render();
