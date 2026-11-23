# MacDesk 后端（src/Core）说明 —— 给接手后端的人 / AI

MacDesk 分三层：

| 目录 | 角色 | 能引用什么 |
|---|---|---|
| `src/Core` | **后端**：数据模型、存储、入口启动、应用发现、图标提取 | 只能用 `System`、`System.Drawing`、`System.Web.Extensions`、`Microsoft.CSharp`。**不能**引用 `System.Windows.Forms`，也不能引用 `src/UI` 里的任何类型 |
| `src/UI` | 前端：窗口、动画、渲染、对话框 | 可以调用 Core；**只能通过 `DeskService` 改数据** |
| `src/Host` | 程序入口、自测、挂到 Explorer 桌面 | 两者都可用 |

这条边界由构建保证，两个方向都检查：

- `build.ps1` 第一步**单独编译** `src/Core` 为 `MacDesk.Core.dll`，且不给它 WinForms 引用，后端一旦依赖 UI，构建直接失败。
- 第二步扫描 `src/UI`：出现 `EntryServices.`、`IconProvider.`、`DesktopStore.`、`new DesktopStore`、`new AppCatalog` 就失败。前端只能用 `DeskService` 和纯数据类型（`DeskItem`、`DeskTodo`、`TodoQueries` 等）。

最终产物仍是一个 `MacDesk.exe`。

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
DesktopData { version: 2, groups: DeskGroup[], notes: {"yyyy-MM-dd": string}, todos: DeskTodo[], showLabels, homeX, homeY }
DeskGroup   { id, title, subtitle, items: DeskItem[], desktopX, desktopY }   // 桌面按 groups 的顺序排在固定网格里；desktopX/Y 仅为兼容旧数据保留（启动时 MigratePinnedPositionsToOrder 换算成顺序并清为 -1）；subtitle 界面不再显示
DeskItem    { id, title, description, type: "app"|"file"|"folder"|"url", target, icon, arguments }
DeskTodo    { id, title, done, due: ""|"yyyy-MM-dd", created, completed }   // 提醒事项；列表顺序即显示顺序；created/completed 是 UTC ISO-8601
```

- 小写字段名就是 JSON 字段名，**不要改名**；要改结构请提升 `version` 并在 `DesktopStore.Migrate` 里做迁移。
- 版本 1 → 2：读入时在内存里补上空的 `todos`，下一次保存写成版本 2。旧版 exe 读到版本 2 会拒绝加载（不会悄悄丢掉提醒事项）。
- 提醒事项校验：标题非空、不超过 200 字、不含换行和控制字符；`due` 为空或真实存在的日期；最多 5000 条；id 与分类、条目共用同一个 id 空间。
- id 只允许 `[A-Za-z0-9_-]`，全局唯一（分类和条目共用一个 id 空间），新 id 用 `DesktopStore.NewId()`。
- `target`：`url` 必须是完整 http/https；其余类型必须是绝对路径（`C:\…` 或 `\\server\share\…`）。
- 校验规则全部在 `DesktopStore.Validate`，任何写入都会经过它。

## 存储保证（`DesktopStore.cs`）

`Edit(mutation)`：克隆 → 修改 → 校验 → 序列化 → 跨进程互斥锁 → 与磁盘 SHA256 比对（被外部改过就拒绝）→ 写带时间戳的备份到 `Backups\` → `File.Replace` 原子替换。任何一步失败，内存和磁盘都保持原样。

备份保留：每次保存成功后清理 `Backups\`，保留最新 50 份，再加最近 30 天里每天最新的 1 份。清理失败不影响保存，文件名不符合 `data-yyyyMMdd-…json` 的文件不会被动。

## 唯一入口：`DeskService`

前端持有一个 `DeskService`（`AppController.Service`），订阅 `Changed` 刷新界面。所有方法都同步执行，**失败抛异常、成功后触发 `Changed`**。

`Changed` 的参数是 `DeskChangedEventArgs`，`Kinds` 说明这次改了什么：`Groups`（文件夹与入口）、`Notes`（日历备注）、`Todos`（提醒事项）、`Prefs`（偏好）；`Reload` 为 `All`。处理函数用 `DeskChangedEventArgs.KindsOf(e)` 读取，界面据此只刷新相关视图（勾选提醒事项不会重画桌面文件夹）。

| 方法 | 作用 |
|---|---|
| `Data` / `Root` | 当前数据（只读，每次修改后整体替换）/ 数据目录 |
| `Reload()` | 从磁盘重新读取 |
| `SeedStarterGroupsIfNew()` | 全新目录时，从开始菜单生成 4 个初始文件夹 |
| `GetGroup(id)` / `FindItem(groupId, itemId)` | 查询（前者找不到抛异常，后者返回 null） |
| `AddGroup(group)` / `CreateGroupWithUniqueTitle(baseTitle)` | 新建文件夹 |
| `RenameGroup(id, title)` / `UpdateGroup(id, 旧标题, 旧副标题, 新标题, 新副标题)` | 改名；后者是乐观并发（中途被改过就拒绝） |
| `DeleteGroup(id)` / `MoveGroup(id, insertIndex)` | 删除（不碰原文件）/ 排序 |
| `MigratePinnedPositionsToOrder()` | 旧版的自由坐标 → 按阅读顺序重排并清除坐标（启动时调用一次） |
| `AddPaths(groupId, paths, index)` / `AddUrl(groupId, url, index)` / `AddItem(groupId, item)` | 添加入口（只记录路径，绝不移动或复制文件） |
| `UpdateItem(groupId, itemId, 编辑前, 编辑后)` / `RenameItem(...)` / `DeleteItem(...)` | 修改、重命名、删除入口 |
| `MoveItem(源分类, itemId, 目标分类, insertIndex)` | 排序或跨文件夹移动；insertIndex 是移动**前**目标列表中的插入位置，-1 = 末尾 |
| `ToggleCandidate(groupId, candidate)` | 添加页的勾选：没有同 target 的就加入（新 id），有就移除；返回是否已加入 |
| `SetNote(dateKey, text)` / `GetNote(dateKey)` | 日历备注（空文本 = 删除；内容没变则不保存） |
| `AddTodo(title, due)` → `DeskTodo` | 新提醒事项（`due` 为 `""`/null 表示没有日期） |
| `RenameTodo` / `SetTodoDue` / `DeleteTodo` / `FindTodo` | 修改、改日期、删除、查询（后者找不到返回 null） |
| `SetTodoDone(id, done)` / `SetTodosDone(字典)` | 完成或恢复；后者把多次勾选合成一次保存（界面在动画结束后批量调用） |
| `SetHomePosition(x, y)` / `ToggleShowLabels()` | 偏好 |
| `Launch(groupId, itemId)` | 通过 Explorer 的 ShellExecute 打开 |
| `OpenAppOnWorker(item)`（静态） | 在工作线程上启动应用（解析快捷方式或提权确认可能较慢） |
| `OpenWith(groupId, itemId, EntryAction)` / `HasEditor(EntryAction)` | 在终端、VS Code、Cursor 中打开，或在资源管理器中显示 |
| `OpenFolder(DataFolder)` | 打开桌面目录或备份目录 |
| `DiscoverApps()` / `Catalog` | 扫描开始菜单 / 桌面的快捷方式（缓存约 45 秒）/ 添加页使用的应用目录 |
| `GetIcon(item)` / `LoadIcon(item)`（静态） | 128px 图标（带缓存，不会返回 null）/ 一张新的、由调用方释放的图标 |
| `NewId()` / `ValidateTarget()` / `LooksLikeTarget()` / `MaxTodoTitle`（静态） | 新 id、与保存时一致的目标校验、判断文本是否像路径或网址、提醒事项标题长度上限 |

`TodoQueries`（纯函数，“今天”由调用方传入）：`Agenda`（逾期 → 今天 → 未定日期，只含未完成）、`ForDay`（某天的提醒事项，未完成在前）、`DayMarkers`（每天有没有未完成的提醒事项或备注）。

`EntryServices`、`IconProvider`、`DesktopStore` 是后端内部实现，前端不能直接调用（构建会检查）。

### 错误约定

- `InvalidDataException`：输入不合法，或目标已被删除（如“分类已删除。”）
- `IOException`：文件被外部修改 / 无法写入 / 另一个进程正在保存
- `FileNotFoundException`：启动的目标已不存在

异常消息是**直接给用户看的中文**，前端原样显示（`AppController.Fail`）。

### 线程

只从 UI 线程调用。`Changed` 在调用线程上同步触发。`DiscoverApps` / `GetIcon` / `LoadIcon` / `OpenAppOnWorker` 可以在后台线程调用。

## 新增一个后端能力的步骤

1. 在 `DeskService` 加方法：用私有 `Edit(DeskChange.种类, d => …)` 修改数据（它会负责校验、保存、触发 `Changed`）。
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
