# MacDesk 后端（src/Core）说明 —— 给接手后端的人 / AI

MacDesk 分三层：

| 目录 | 角色 | 能引用什么 |
|---|---|---|
| `src/Core` | **后端**：数据模型、存储、入口启动、应用发现、图标提取 | 只能用 `System`、`System.Drawing`、`System.Web.Extensions`、`Microsoft.CSharp`。**不能**引用 `System.Windows.Forms`，也不能引用 `src/UI` 里的任何类型 |
| `src/UI` | 前端：窗口、动画、渲染、对话框 | 可以调用 Core；**只能通过 `DeskService` 改数据** |
| `src/Host` | 程序入口、自测、挂到 Explorer 桌面 | 两者都可用 |

这条边界由编译保证：`build.ps1` 第一步会**单独编译** `src/Core` 为 `MacDesk.Core.dll`，且不给它 WinForms 引用，后端一旦依赖 UI，构建直接失败。最终产物仍是一个 `MacDesk.exe`。

## 构建与测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1            # 编译 + 部署到 Documents\MacDesk
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoDeploy  # 只编译到 build\
.\build\MacDesk.exe --self-test   # 退出码 0 = 通过；失败原因写在 exe 旁的 self-test-error.log
.\build\MacDesk.exe --bench       # 前端离屏性能测试，结果写在 exe 旁的 bench.txt
```

后端测试在 `src/Core/CoreTests.cs`。**新增或修改后端行为时，在这里补测试**，并保证 `--self-test` 仍返回 0。
注意：语言版本是 C# 4（`/langversion:4`），没有 `?.`、`$""`、`nameof`、`async`、表达式体成员。

## 数据模型（`Models.cs`，即 `%LOCALAPPDATA%\MacDesk\desktop.json`）

```text
DesktopData { version: 1, groups: DeskGroup[], notes: {"yyyy-MM-dd": string}, showLabels, homeX, homeY }
DeskGroup   { id, title, subtitle, items: DeskItem[], desktopX, desktopY }   // desktopX/Y = -1 表示自动排布
DeskItem    { id, title, description, type: "app"|"file"|"folder"|"url", target, icon, arguments }
```

- 小写字段名就是 JSON 字段名，**不要改名**；要改结构请提升 `version` 并在 `DesktopStore.Reload` 里做迁移。
- id 只允许 `[A-Za-z0-9_-]`，全局唯一（分类和条目共用一个 id 空间），新 id 用 `DesktopStore.NewId()`。
- `target`：`url` 必须是完整 http/https；其余类型必须是绝对路径（`C:\…` 或 `\\server\share\…`）。
- 校验规则全部在 `DesktopStore.Validate`，任何写入都会经过它。

## 存储保证（`DesktopStore.cs`）

`Edit(mutation)`：克隆 → 修改 → 校验 → 序列化 → 跨进程互斥锁 → 与磁盘 SHA256 比对（被外部改过就拒绝）→ 写带时间戳的备份到 `Backups\` → `File.Replace` 原子替换。任何一步失败，内存和磁盘都保持原样。

## 唯一入口：`DeskService`

前端持有一个 `DeskService`（`AppController.Service`），订阅 `Changed` 刷新界面。所有方法都同步执行，**失败抛异常、成功后触发 `Changed`**。

| 方法 | 作用 |
|---|---|
| `Data` / `Root` | 当前数据（只读，每次修改后整体替换）/ 数据目录 |
| `Reload()` | 从磁盘重新读取 |
| `SeedStarterGroupsIfNew()` | 全新目录时，从开始菜单生成 4 个初始文件夹 |
| `GetGroup(id)` / `FindItem(groupId, itemId)` | 查询（前者找不到抛异常，后者返回 null） |
| `AddGroup(group)` / `CreateGroupWithUniqueTitle(baseTitle)` | 新建文件夹 |
| `RenameGroup(id, title)` / `UpdateGroup(id, 旧标题, 旧副标题, 新标题, 新副标题)` | 改名；后者是乐观并发（中途被改过就拒绝） |
| `DeleteGroup(id)` / `MoveGroup(id, insertIndex)` | 删除（不碰原文件）/ 排序 |
| `SetGroupPosition(id, x, y, 当前布局, 区域)` / `ResetGroupPositions()` | 桌面位置 / 回到自动网格 |
| `AddPaths(groupId, paths, index)` / `AddUrl(groupId, url, index)` / `AddItem(groupId, item)` | 添加入口（只记录路径，绝不移动或复制文件） |
| `UpdateItem(groupId, itemId, 编辑前, 编辑后)` / `RenameItem(...)` / `DeleteItem(...)` | 修改、重命名、删除入口 |
| `MoveItem(源分类, itemId, 目标分类, insertIndex)` | 排序或跨文件夹移动；insertIndex 是移动**前**目标列表中的插入位置，-1 = 末尾 |
| `ToggleCandidate(groupId, candidate)` | 添加页的勾选：没有同 target 的就加入（新 id），有就移除；返回是否已加入 |
| `SetNote(dateKey, text)` / `GetNote(dateKey)` | 日历备忘（空文本 = 删除） |
| `SetHomePosition(x, y)` / `ToggleShowLabels()` | 偏好 |
| `Launch(groupId, itemId)` | 通过 Explorer 的 ShellExecute 打开 |
| `DiscoverApps()` | 扫描开始菜单 / 桌面的快捷方式（缓存约 45 秒） |
| `GetIcon(item)` | 128px 图标（保留透明度，带缓存，不会返回 null） |

`EntryServices`（打开所在文件夹、终端、VS Code / Cursor 等）和 `IconProvider` 是 `DeskService` 用到的静态工具，前端也可以直接调用其中的只读 / 启动类方法。

### 错误约定

- `InvalidDataException`：输入不合法，或目标已被删除（如“分类已删除。”）
- `IOException`：文件被外部修改 / 无法写入 / 另一个进程正在保存
- `FileNotFoundException`：启动的目标已不存在

异常消息是**直接给用户看的中文**，前端原样显示（`AppController.Fail`）。

### 线程

只从 UI 线程调用。`Changed` 在调用线程上同步触发。`DiscoverApps` / `GetIcon` 自带锁，可以在后台线程预取。

## 新增一个后端能力的步骤

1. 在 `DeskService` 加方法：用私有 `Edit(d => …)` 修改数据（它会负责校验、保存、触发 `Changed`）。
2. 需要新字段时：改 `Models.cs`，必要时改 `DesktopStore.Validate`，旧 JSON 要能继续读（给默认值）。
3. 在 `CoreTests.Run` 里加测试，跑 `--self-test`。
4. 前端调用：在 `AppController` 加一个包装（`Try(delegate { Service.Xxx(...); })`），界面里调用这个包装。

## 硬性规则

- 永远不删除、不移动、不修改用户的原文件；“删除”只删入口。
- 不改 Explorer、不装全局钩子、不改系统设置。
- 后端不弹窗、不访问界面；需要提示用户就抛异常。
- 不要在 `desktop.json` 里存绝对屏幕像素以外的界面状态（动画、悬停等都属于前端）。

## 适合交给后端做的方向（未实现）

- 监听 `desktop.json` 被外部修改（FileSystemWatcher）后自动 `Reload`。
- 应用发现支持 UWP / Microsoft Store 应用（`shell:AppsFolder`）。
- 图标的磁盘缓存，加快冷启动。
- 导入 / 导出配置、从备份一键恢复。
- 多显示器：按显示器保存文件夹位置。
