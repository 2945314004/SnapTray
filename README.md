# 轻截 SnapTray

Windows 托盘截图 + 桌面贴图工具，C# / WPF / .NET 8。启动后常驻通知区，不显示主窗口；
全局热键（默认 <kbd>Alt</kbd> + <kbd>`</kbd>）唤出全屏遮罩，拖拽划区、单击整窗、或一键全屏，
截完在选区旁边弹出操作栏：**复制 / 保存 / 置顶 / 取消**。

```
┌─ 全屏遮罩（桌面快照压暗，选区镂空）──────────────────────────────┐
│   拖动选择区域 · 单击截取窗口 · Esc 取消                          │
│   [ 当前屏幕 (F) ] [ 全部屏幕 (Ctrl+F) ] [ × 取消 ]               │
│                                                                  │
│        ┌─ 拖出的选区（边框高亮 + 尺寸角标 560 × 440 像素）─┐      │
│        │                                                  │      │
│        └──────────────────────────────────────────────────┘      │
│        [ ✓ 复制 ] [ 保存 ] [ 置顶 ] [ × 取消 ]                    │
└──────────────────────────────────────────────────────────────────┘
```

## 直接下载（无需编译）

**[SnapTray-1.1.1-win-x64.zip](https://github.com/2945314004/SnapTray/releases/download/v1.1.1/SnapTray-1.1.1-win-x64.zip)** · 64 MiB（单个 exe）

解压后双击 `SnapTray.exe` 就能用。自包含单文件，**不需要安装 .NET 运行时**；Windows 10 1809+ / Windows 11，x64。
exe 未做代码签名，首次运行会弹 SmartScreen 蓝色提示：点「更多信息」→「仍要运行」。

完整的版本列表见 [Releases](https://github.com/2945314004/SnapTray/releases)。

> **开机自启是完全静默的**：注册表里写的是 `"…\SnapTray.exe" --startup`，程序本身是 GUI 子系统，
> 登录时不会出现任何控制台窗口、也不会弹对话框（快捷键被占用之类的提示只走托盘气泡）。
> 如果登录时仍看到黑框，那不是本程序——它启动时的子进程数为 0，可用
> `tools/check-autostart.ps1` 列出全部自启项并标出哪些是控制台程序。

## 一、环境检查结果

本机已确认（`dotnet --list-sdks`）：**8.0.426** 与 9.0.313，`global.json` 锁定 8.0.426（`rollForward: latestPatch`）。
无需 Visual Studio，纯 `dotnet` CLI 即可构建；也没有任何第三方 NuGet 依赖（只用 BCL + WPF/WinForms 内置程序集）。

如果换到一台没有 SDK 的机器：

```powershell
winget install Microsoft.DotNet.SDK.8
# 或下载 https://dotnet.microsoft.com/download/dotnet/8.0 的 SDK x64 安装包
dotnet --list-sdks   # 输出里出现 8.0.x 即就绪
```

运行时（只跑不编译）需要 **.NET 8 Desktop Runtime**：`winget install Microsoft.DotNet.DesktopRuntime.8`。
若用自包含发布（见下）则目标机器什么都不用装。

## 二、构建与运行

```powershell
dotnet build SnapTray.sln -c Release          # 构建主项目 + 冒烟测试
dotnet run --project src/SnapTray             # 直接运行（托盘出现即成功）
.\src\SnapTray\bin\Release\net8.0-windows\SnapTray.exe
```

命令行参数（便于脚本/开机项调用）：

| 参数 | 效果 |
| --- | --- |
| `--capture` | 启动后立即进入区域截图 |
| `--fullscreen` | 启动后立即选中当前屏幕全屏 |
| `--settings` | 启动后直接打开设置窗口 |

带参数启动时如果程序已常驻托盘，新进程会把请求转交给已有实例（命名事件）后立即退出，
不会出现第二个托盘图标，也不会因为抢占热键失败而报错——可以直接做桌面快捷方式当截图按钮用。

发布单文件（目标机器无需安装 .NET）：

```powershell
dotnet publish src/SnapTray -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

> 不要对 WPF 用 `PublishTrimmed`，裁剪会破坏 XAML 反射和 WinForms 互操作。

## 三、功能与快捷键

截图遮罩上：

| 操作 | 效果 |
| --- | --- |
| 拖拽 | 划区截图，松开即出现操作栏 |
| **单击某个窗口** | 直接选中该窗口的可见范围（DWM 边框，不含阴影） |
| <kbd>F</kbd> / 按钮「当前屏幕」 | 选中光标所在显示器 |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> / 按钮「全部屏幕」 | 选中整个虚拟桌面 |
| <kbd>Enter</kbd> | 复制到剪贴板 |
| <kbd>Esc</kbd> / 右键 / 按钮「× 取消」 | 取消，不产生任何结果 |

操作栏：**✓ 复制**（写剪贴板）、**保存**（另存为 PNG，默认落在设置目录）、**置顶**（无边框贴图窗口）、**× 取消**。

贴图窗口：拖动移动、滚轮缩放、双击还原 1:1、<kbd>Ctrl</kbd>+<kbd>C</kbd>/<kbd>Ctrl</kbd>+<kbd>S</kbd> 复制/另存、
<kbd>Esc</kbd> 或 × 关闭（关闭即销毁）。

托盘图标：双击 = 区域截图；右键菜单 = 区域截图 / 全屏截图 / 打开保存目录 / 设置 / 退出。

设置窗口右上角显示当前版本号（取自程序集，发版时随 `<Version>` 自动更新）。

设置项：开机自启（写 `HKCU\...\CurrentVersion\Run`，路径整体加引号并带 `--startup`）、自定义快捷键（Alt/Ctrl/Shift +
任意主键，应用后立即生效）、默认保存目录。配置持久化在 `%AppData%\SnapTray\settings.json`（原子替换写入，
JSON 损坏时自动备份为 `settings.corrupt-*.json` 并回退默认值）。

自启项每次启动都会自检：路径对不上（程序搬家、或旧版本写入过 `dotnet.exe` 形式）就改写成当前 exe——
后者正是登录时闪控制台的典型原因，代码里已彻底禁止生成控制台宿主命令行。

## 三·五、标注、取字与滚动截图

划好区之后，工具栏第二排就是编辑能力，全部作用在**选区内的物理像素**上，导出时按 1:1 烧进图片。

| 工具 | 快捷键 | 说明 |
| --- | --- | --- |
| 矩形 / 椭圆 | `R` / `E` | 拖出边框，线宽随显示器缩放自适应 |
| 箭头 | `A` | 自动生成箭头头部，短线也不会出现头比线细 |
| 画笔 | `B` | 自由曲线，圆头圆角；可切换**细 / 中 / 粗**三档笔头 |
| 马赛克 | `M` | 按 10px 网格采样原图色块；笔刷同样是**细 / 中 / 粗**三档 |
| 文字 | `T` | 单击落点输入，`Enter` 确认、`Shift+Enter` 换行、`Esc` 取消 |
| 撤销 | `Ctrl+Z` | 逐笔回退 |
| 取字 | `Ctrl+E` | 系统自带离线 OCR（`Windows.Media.Ocr`），中文识别，不联网、不要 Key |
| 翻译 | `Ctrl+T` | 先 OCR 再翻译，自动判断中英方向，依次尝试有道 / MyMemory / Google |
| 滚动截图 | `Ctrl+L` | 逐屏向下滚动拼接长图，`Esc` 结束 |
| 保存 / 置顶 / 完成 | `Ctrl+S` / `Ctrl+P` / `Enter` | 与按钮等价 |

选中画笔或马赛克时，颜色圆点右边会出现「粗细 ○ ● ○」，细/中/粗分别对应
画笔 2/4/8 px 线宽、马赛克 14/26/44 px 笔刷直径（都按显示器缩放换算，跨 DPI 视觉一致）。

**标注为什么能"所见即所存"**：标注对象只存一份坐标（选区内的物理像素），
屏幕预览由 `AnnotationCanvas` 按显示器缩放渲染，导出时 `AnnotationRenderer.Compose` 按 1:1 渲染图层再叠到位图上——
同一份绘制代码（`Models/Annotation.cs`）供两处调用，所以不会出现"屏幕上对、导出来错位"。

**滚动截图的原理与边界**：藏起遮罩 → 逐屏抓图 → 用行特征找出两帧之间的滚动像素数 → 只拼接新露出的那几行。
纯像素方案，不注入钩子，浏览器 / 记事本 / PDF 阅读器通用；但页面若有吸顶导航、悬浮按钮或滚动动画，
接缝处可能留下重复条带——每次滚动前会等画面稳定再抓，能缓解但不能根除。

## 四、实现要点

**多显示器与 DPI。** `app.manifest` 声明 `PerMonitorV2`，因此 UI 线程拿到的 Win32 坐标就是物理像素。
所有选区、裁剪、贴图定位都在**物理像素**里算，只有 WPF 窗口的 `Width/Height/Left/Top`（DIP）才做换算：

- 遮罩不用 `Window.Left/Top` 定位（跨屏时 DIP 无统一比例），而是 `SetWindowPos` 按目标显示器的物理矩形摆放，
  再用 `Surface.ActualWidth / MonitorBounds.Width` 把物理矩形映射到本窗口的 DIP 坐标——**每块屏各自换算**，
  150% 屏和 100% 屏混插也不会累积误差。
- 一张快照 = 整个虚拟桌面的位图，原点可能是负坐标（副屏在主屏左侧/上方）。裁剪前先减去虚拟桌面原点。
- 贴图窗口跨屏拖动时监听 `DpiChanged`，按新旧 DPI 比例换算窗口尺寸，避免物理尺寸跳变。

**屏幕捕获。** `Graphics.CopyFromScreen` 一次性抓取虚拟桌面（在显示任何遮罩之前），之后所有预览、裁剪、
导出都用这张冻结快照，保证"所见即所截"。`CopyPixelOperation` 只能传单个值，组合 `CaptureBlt` 会被参数校验拒绝。

**全局热键。** `RegisterHotKey` 绑在一个 `HWND_MESSAGE` 消息专用窗口上（无 UI、无任务栏），
加 `MOD_NOREPEAT` 防止长按连发；改键时先注册新 ID 再注销旧 ID，注册失败时旧热键保持有效，
并把 `ERROR_HOTKEY_ALREADY_REGISTERED (1409)` 翻译成"被其他程序占用，请更换组合"。
<kbd>F12</kbd> 被 Windows 调试器保留，注册必然失败，界面会提示换键。

**窗口截图。** 不能使用 `WindowFromPoint`——遮罩自己是覆盖全屏的置顶窗口，永远只会命中自己。
改为 `EnumWindows` 按 Z 序自上而下遍历，跳过本进程、不可见/最小化、DWM cloaked（UWP 隐藏宿主）、
以及 `Progman`/`WorkerW` 桌面壳窗口，取第一个包含光标的窗口，再用
`DWMWA_EXTENDED_FRAME_BOUNDS` 拿真实可见边框（`GetWindowRect` 会多出一圈不可见阴影）。

## 五、项目结构

```
src/SnapTray/
  App.xaml(.cs)            托盘生命周期、会话编排、设置应用与回滚
  Assets/SnapTray.ico      多尺寸应用图标（tools/make_icon.py 生成）
  Models/                  AppSettings、HotkeyGesture、CaptureResult
  Services/                HotkeyManager、ScreenCaptureService、CaptureSession、
                           SettingsService、StartupService、ClipboardService、
                           ImageFileService、TrayIconManager
  Helpers/                 NativeCapture（显示器/光标/遮罩定位）、WindowLocator（窗口命中）、
                           ScreenGeometry（纯物理像素几何）、WindowPosition（贴图摆放）
  Views/                   OverlayWindow、PreviewWindow、PinWindow、SettingsWindow
  ViewModels/              SettingsViewModel
tests/SnapTray.SmokeTests/ 无第三方框架的集成测试（13 项）
tools/                     开发自检脚本（图标生成 / 屏幕截图 / 输入注入 / 剪贴板探测）
```

## 六、测试与验收

```powershell
dotnet run --project tests/SnapTray.SmokeTests -c Release            # 13 项（其中真实桌面那项跳过）
dotnet run --project tests/SnapTray.SmokeTests -c Release -- --capture  # 额外抓一次真实桌面校验尺寸
```

覆盖：配置读写/损坏恢复/原子替换、负坐标与跨屏裁剪（逐像素比对）、几何边界、
热键校验与注册生命周期（冲突保留旧绑定、改键释放旧热键、销毁释放最终热键）。

已在本机做过端到端验收（`tools/` 下的脚本驱动真实鼠标键盘事件，逐项截图核对）：

- 拖拽 560×440 → 操作栏出现 → <kbd>Enter</kbd> 复制 → 剪贴板 `CF_DIB` 为 **560×440 @32bpp**（尺寸与选区一致）。
- 单击窗口 → 整窗选中（1920×1012）→ 操作栏出现在选区下方。
- 置顶 → 贴图窗口出现并可滚轮放大 1.33×。
- `--fullscreen` → 选中 1920×1080。
- 已常驻托盘时再执行 `SnapTray.exe --capture` → 新进程退出码 0，常驻实例弹出遮罩（进程数仍为 1）。

## 七、已知限制

- 屏幕捕获依赖 GDI `CopyFromScreen`：受保护内容（DRM/部分全屏独占游戏）会截到黑屏；这是系统限制。
- 遮罩显示的是按下热键那一刻的快照，之后桌面变化不会实时更新（避免录屏软件式的持续抓屏开销）。
- 不支持滚动截图、标注涂鸦、OCR——按需求范围未做。
