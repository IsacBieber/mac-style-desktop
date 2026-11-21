# 架构与实现要点

## 0. 分层

- `src/Core`：后端（数据、存储、启动、应用发现、图标）。唯一入口是 `DeskService`，契约见 [BACKEND_API.md](BACKEND_API.md)。构建时会单独编译以确保它不依赖 UI。
- `src/UI`：前端（下面各节描述的窗口、材质、动画）。只通过 `AppController.Service`（`DeskService`）读写数据，订阅它的 `Changed` 刷新。
- `src/Host`：`Program.cs`（入口、`--self-test`、`--bench`、桌面层级维护）与 `NativeDesktopHost.cs`（挂到 Explorer 桌面视图）。

## 1. 窗口拓扑

- `HomeForm`：左侧时钟 / 日历小组件，**不**作为独立顶层窗口，而是挂在桌面 `SHELLDLL_DefView` 下的 `WS_CHILD`，并保持在 `SysListView32` 之前。
- `DesktopFolderForm`：桌面文件夹画布，同样是 `SHELLDLL_DefView` 的子窗口。它还负责在文件夹打开期间接管键盘焦点（见第 4 节）。
- `BackdropOverlay`：文件夹打开时铺满工作区的模糊 + 压暗背景。`WS_EX_NOACTIVATE`，点击会被吃掉并关闭文件夹（同 iOS 点外部）。
- `GlassMotion`：开合动画期间的形变层，`WS_EX_LAYERED | WS_EX_NOACTIVATE`，由独立渲染线程逐帧 `UpdateLayeredWindow`。
- `PanelForm`：打开后的文件夹面板，本程序自己的顶层 layered 窗口，`WS_EX_NOACTIVATE`，owner 为程序内部隐藏窗口，绝不被 Explorer 设为 owner。

不写 Shell 的 Z 序、不设置全局 topmost、不安装任何全局钩子。

## 2. 毛玻璃

不再使用 Win11 Acrylic（它无法动画，也给不了大圆角抗锯齿）。所有材质都由程序自己画，保证形变层与最终面板像素一致：

- **桌面材质**（`Wallpaper`）：按注册表里的壁纸路径与 `WallpaperStyle`（填充 / 适应 / 拉伸 / 居中 / 平铺）在主显示器上重建壁纸，降采样 → 三次盒式模糊 → 提高饱和度 → 放大，得到一张全屏模糊图。文件夹图标与小组件用 `TextureBrush` 按自身屏幕位置取样，再叠一层浅色 / 深色色调。原图丢失时回退到 `TranscodedWallpaper`。
- **展开背景**（`Backdrop.FromScreen`）：鼠标按下的瞬间在线程池里截屏并模糊，同时预先合成压暗版本；松开时直接拿来用。
- 圆角统一用连续曲率的 `Ui.Squircle`（三次贝塞尔近似），所有 layered 窗口都是 32bpp 预乘 alpha，边缘自带抗锯齿。

## 3. 动画

Windows 11 会把 WM_TIMER 合并到约 15.6ms，用定时器最多约 65fps。现在的做法：

1. **展开 / 收起**：专属线程，每帧呈现后调用 `DwmFlush()` 等下一次合成，即按显示器刷新率出帧（本机 240Hz），不空转 CPU。曲线是解析解的阻尼弹簧：展开 response 0.36s、阻尼 0.8（约 1.5% 回弹，380ms），收起临界阻尼（300ms）。
2. **其余动效**（悬停、按下、回流、滚动、搜索栏、标题淡入、月份滑动）：`FramePump` 同样以 `DwmFlush` 为节拍向 UI 线程投递帧；每个属性是一个 `Spring`（SwiftUI 式参数），固定步长积分，60Hz 与 240Hz 下结果一致；全部静止后自动停泵。
3. **渲染成本**：`LayeredSurface` 复用同一块 DIB（形变层预留最终尺寸，不在动画中重新分配）；桌面画布只提交脏矩形（`UpdateLayeredWindowIndirect` + `prcDirty`）；面板的背景、图标（64px 预缩放）、文字标签都预渲染缓存，每帧只做拷贝。
4. **交接**：形变层第一帧画的就是桌面文件夹图标本身，最后一帧与面板同材质、同快照；结束时先呈现面板再销毁形变层，所以没有跳变。
5. 每次动画有代际编号，旧渲染线程排队的「完成」消息不会作用到新动画上。

## 4. 焦点与键盘

文件夹 UI 的所有窗口都**不激活**。原因：在 Win+D（显示桌面）状态下，任何其他窗口获得激活都会让 Windows 退出该状态并把所有窗口还原到最前，文件夹随即被盖住。

键盘输入改由 `DesktopFolderForm` 接收：它是桌面视图的子窗口，与 Explorer 桌面线程共享输入队列，可以在桌面保持激活的同时拿到焦点。打开文件夹时 `TakeKeyboardFocus`，关闭时把焦点还给原来的桌面控件。按键经 `AppController.RouteKey / RouteChar` 转给面板；重命名在面板内自绘输入框，并用 `ImmSetCompositionWindow / ImmSetCandidateWindow` 把输入法窗口放到输入位置。

若其他程序成为前台（`GetForegroundWindow` 不是桌面也不是本进程），面板自动收起，不会留下一层压暗背景。

## 5. 拖放

- 纯文本拖放格式 `MacDeskNative.Item` = `sourceGroupId + "\n" + itemId`。
- 外部文件走标准 `FileDrop`，通过 `EntryServices.FromPath` 生成入口（只记录路径，绝不移动原文件）。
- 面板内排序时，可视槽位先映射回真实下标再保存（搜索过滤状态下槽位 ≠ 下标）。

## 6. 存储

`DesktopStore`：

- 读入后克隆一份数据供 UI 使用；
- `Edit` 在克隆上修改、校验、序列化，再用 SHA256 比对磁盘内容，最后原子替换并写带哈希的备份到 `Backups`；
- 任何一步失败都回滚内存数据，不会出现「界面显示成功但磁盘没写」。

## 7. 时区

时钟固定北京 UTC+8：读取 UTC 时间后 `AddHours(8)`，不依赖系统时区，也不修改系统设置。`BeijingFromUtc` 会拒绝非 UTC 的输入，避免重复偏移。

## 8. 诊断

设置环境变量 `MACDESK_MOTION_LOG=<文件路径>` 启动后，每次开合会追加一行 JSON：帧数、最大帧间隔、每帧平均 / 最大耗时，以及从点击到第一帧的准备时间 `setupMs`。

设置环境变量之外，`MacDesk.exe --bench` 会在离屏状态下跑真实渲染代码（不显示窗口、不读写你的 desktop.json），给出桌面文件夹悬停、日历悬停 / 翻月、添加页滚动、面板悬停的每帧耗时。240Hz 下每帧预算约 4.2ms。
