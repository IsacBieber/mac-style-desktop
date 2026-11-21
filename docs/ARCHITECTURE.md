# 架构与实现要点

## 1. 窗口拓扑

- `HomeForm`：左侧时钟/月历卡片（352×534，位置 48,64），**不**作为独立顶层窗口，而是挂在桌面 `SHELLDLL_DefView` 下的 `WS_CHILD`。
- `DesktopFolderForm`：文件夹格子网格，同样是 `SHELLDLL_DefView` 的子窗口，随桌面滚动/分辨率自适应。
- `PanelForm`：展开后的玻璃面板，是**本程序自己的**顶层弹出窗口，owner 为程序内部隐藏窗口，绝不被 Explorer 设为 owner。
- `GlassMotion`：开合动画期间的视觉表面，一个 `WS_EX_LAYERED` 的自有窗口，用 `UpdateLayeredWindow` 逐帧呈现缩放后的快照。
- `DimOverlay`：背景压暗层，点击穿透、不抢焦点，只在面板展开期间存在。

不写 Shell 的 Z 序、不设置全局 topmost、不安装任何全局钩子（键盘只在自己窗口内处理）。

## 2. 毛玻璃

Win11 的 per-window Acrylic：对本程序自有窗口调用 `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` 并 `DwmExtendFrameIntoClientArea(-1)`。

关键点：`Graphics.Clear(Color.Black)` 会把 alpha 写成 255，从而把合成器玻璃整个盖住。因此背景必须用 GDI `FillRect` + `BLACK_BRUSH` 清成 **alpha=0**，让 Acrylic 透出来。

面板圆角交给 DWM 的 `DWMWA_WINDOW_CORNER_PREFERENCE` 裁剪（自带抗锯齿），描边半径与之对齐，避免手工 `Region` 造成的阶梯锯齿。

## 3. 动画

Windows 11 会把 WM_TIMER 与线程池定时器合并到约 15.6ms，用定时器最多只能到约 65fps。因此动画由**专属线程**驱动：

1. 线程用 `Stopwatch` 按 1/120 秒对齐节拍；
2. 每帧自己计算目标矩形与透明度，直接 `UpdateLayeredWindow`，不经过消息队列；
3. 结束/取消时一次性 `BeginInvoke` 回 UI 线程做收尾；
4. 反向（Esc / 点外部 / 再点来源文件夹）由渲染线程就地换向，不重建窗口。

面板快照在第一次展开时生成并缓存（`PanelForm.MotionSnapshot`），热启动直接克隆，省掉 30–60ms 的抓屏与离屏绘制。曲线用 Smoothstep（两端速度为 0），展开 260ms、收起 190ms。

## 4. 拖放

- 纯文本拖放格式 `MacDeskNative.Item` = `sourceGroupId + "\n" + itemId`；`MacDeskNative.Group` = `groupId`。
- 外部文件走标准 `FileDrop`，通过 `EntryServices.FromPath` 生成入口（只记录路径，绝不移动原文件）。
- 释放位置决定插入下标；面板与格子都接受拖放并给出插入指示线。

## 5. 存储

`DesktopStore`：

- 读入后克隆一份数据供 UI 使用；
- `Edit` 在克隆上修改、校验、序列化，再用 SHA256 比对磁盘内容，最后原子替换并写带哈希的备份到 `Backups`；
- 任何一步失败都回滚内存数据，不会出现「界面显示成功但磁盘没写」。

## 6. 时区

时钟固定北京 UTC+8：读取 UTC 时间后 `AddHours(8)`，不依赖系统时区，也不修改系统设置。`BeijingFromUtc` 会拒绝非 UTC 的输入，避免重复偏移。

## 7. 已知取舍

- 压缩包内不包含可执行文件，避免二进制与源码不同步。
- 动画期间渲染线程自旋等待刷新，约 260ms 的满载；静止时无额外开销。
- Acrylic 是 Win11 行为，其他系统回退为不透明蓝灰面板。
