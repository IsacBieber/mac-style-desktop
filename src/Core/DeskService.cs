using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace MacDesk.Core {
 // ============================================================================================
 //  DeskService — the backend of MacDesk. The UI (src/UI) never edits DesktopData directly;
 //  every change goes through one of these methods, which validate, save atomically
 //  (DesktopStore.Edit) and then raise Changed. See docs/BACKEND_API.md for the contract.
 //
 //  Error contract: methods throw on failure and leave both memory and disk unchanged.
 //   - InvalidDataException : bad input or the target no longer exists ("分类已删除" etc.)
 //   - IOException          : the file changed underneath us / could not be written
 //   - FileNotFoundException: launching an entry whose target is gone
 //  Messages are user-facing Chinese; the UI shows them as-is.
 //  The UI serializes reminder writes on a worker. The store serializes commits and publishes
 //  read-only snapshots. Changed fires on the calling thread. Other UI commands call the
 //  service on the UI thread unless the method documents worker use.
 // ============================================================================================

 /// What a change touched, so views can skip work that does not concern them.
 [Flags]
 public enum DeskChange { None = 0, Groups = 1, Notes = 2, Todos = 4, Prefs = 8, All = Groups | Notes | Todos | Prefs }

 /// Arguments of DeskService.Changed. Use DeskChangedEventArgs.KindsOf(e) in handlers.
 public sealed class DeskChangedEventArgs : EventArgs {
  public DeskChange Kinds { get; private set; }
  public DeskChangedEventArgs(DeskChange kinds) { Kinds = kinds; }
  /// The kinds carried by `e`, or All when the sender did not say.
  public static DeskChange KindsOf(EventArgs e) { var c = e as DeskChangedEventArgs; return c == null ? DeskChange.All : c.Kinds; }
 }

 /// Extra ways to open an entry (context menu).
 public enum EntryAction { Terminal, VSCode, Cursor, Reveal }
 /// Folders the tray can open.
 public enum DataFolder { Desktop, Backups }

 public sealed class DeskService {
  readonly DesktopStore store;

  public DeskService(string root) {
   store = new DesktopStore(root);
   Catalog = new AppCatalog(store.Root);
  }

  /// Folder holding desktop.json and Backups\.
  public string Root { get { return store.Root; } }
  /// Current data. Treat as read-only; it is replaced (not mutated) on every successful change.
  public DesktopData Data { get { return store.Data; } }
  /// Raised after every successful change (and after Reload) with DeskChangedEventArgs.
  public event EventHandler Changed;
  /// Installed-app catalog for the "add apps" page (refreshes on a worker).
  internal AppCatalog Catalog { get; private set; }

  public void Reload() { store.Reload(); OnChanged(DeskChange.All); }

  void Edit(DeskChange kind, Action<DesktopData> mutation) { store.Edit(mutation); OnChanged(kind); }
  void OnChanged(DeskChange kind) { var h = Changed; if (h != null) h(this, new DeskChangedEventArgs(kind)); }

  /// Longest reminder title the store accepts.
  public const int MaxTodoTitle = DesktopStore.MaxTodoTitle;

  /// A fresh id (letters/digits), unique across folders, entries and reminders.
  public static string NewId() { return DesktopStore.NewId(); }
  /// Validates an entry target the same way saving does; returns the normalized target.
  public static string ValidateTarget(string type, string target, bool mustExist) { return EntryServices.ValidateTarget(type, target, mustExist); }
  /// True if the text looks like something AddPaths / AddUrl would accept (absolute path or http/https).
  public static bool LooksLikeTarget(string text) {
   if (String.IsNullOrWhiteSpace(text)) return false;
   return EntryServices.IsAbsolutePath(text) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
  }

  // ---- Queries -------------------------------------------------------------------------

  public static DeskGroup FindGroup(DesktopData d, string groupId) {
   var g = d.groups.Find(v => v.id == groupId);
   if (g == null) throw new InvalidDataException("分类已删除。");
   return g;
  }
  public DeskGroup GetGroup(string groupId) { return FindGroup(Data, groupId); }
  /// Null when the item no longer exists.
  public DeskItem FindItem(string groupId, string itemId) {
   var g = Data.groups.Find(v => v.id == groupId);
   return g == null ? null : g.items.Find(v => v.id == itemId);
  }

  // ---- First run -----------------------------------------------------------------------

  /// On a brand-new root (no desktop.json), creates four starter folders filled from the
  /// Start menu. Returns true if it seeded.
  public bool SeedStarterGroupsIfNew() {
   if (Data.groups.Count > 0 || File.Exists(Path.Combine(Root, "desktop.json"))) return false;
   var dev = NewGroup("开发空间"); var daily = NewGroup("日常应用"); var games = NewGroup("游戏娱乐"); var tools = NewGroup("系统与工具");
   List<DeskItem> discovered;
   try { discovered = EntryServices.DiscoverSystemApps(); } catch { discovered = new List<DeskItem>(); }
   var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   var extras = new List<DeskItem>();
   foreach (var item in discovered) {
    if (item == null || String.IsNullOrEmpty(item.target) || !used.Add(item.target)) continue;
    string key = (item.title ?? "") + " " + (item.target ?? "");
    if (MatchesAny(key, "code", "cursor", "antigravity", "opencode", "deepseek", "kimi", "unity", "python", "git", "terminal", "powershell", "podman", "docker")) { if (dev.items.Count < 8) dev.items.Add(item); }
    else if (MatchesAny(key, "steam", "完美", "5e", "footl", "游戏", "game", "wegame", "epic")) { if (games.items.Count < 8) games.items.Add(item); }
    else if (MatchesAny(key, "wechat", "微信", "腾讯会议", "todesk", "chrome", "edge", "剪映", "obs", "music", "qq", "browser")) { if (daily.items.Count < 8) daily.items.Add(item); }
    else if (MatchesAny(key, "clash", "radmin", "vpn", "waveform", "工具", "7-zip", "notepad")) { if (tools.items.Count < 8) tools.items.Add(item); }
    else extras.Add(item);
   }
   foreach (var item in extras) {
    if (daily.items.Count < 4) daily.items.Add(item);
    else if (tools.items.Count < 4) tools.items.Add(item);
    else if (dev.items.Count < 4) dev.items.Add(item);
   }
   try {
    Edit(DeskChange.Groups, d => { if (d.groups.Count > 0) return; d.groups.Add(dev); d.groups.Add(daily); d.groups.Add(games); d.groups.Add(tools); });
   } catch { return false; }
   return true;
  }
  static DeskGroup NewGroup(string title) { return new DeskGroup { id = DesktopStore.NewId(), title = title, subtitle = "" }; }
  static bool MatchesAny(string text, params string[] keywords) {
   if (String.IsNullOrEmpty(text)) return false;
   foreach (string kw in keywords) if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
   return false;
  }

  // ---- Folders (groups) ----------------------------------------------------------------

  /// Adds a fully-formed group (e.g. from the edit dialog). Its id must be unique.
  public void AddGroup(DeskGroup group) {
   if (group == null) throw new ArgumentNullException("group");
   Edit(DeskChange.Groups, d => d.groups.Add(group));
  }

  /// Creates an empty folder titled baseTitle, or "baseTitle 2/3/..." if taken. Returns it.
  public DeskGroup CreateGroupWithUniqueTitle(string baseTitle) {
   string title = baseTitle;
   int n = 2;
   while (Data.groups.Exists(v => String.Equals(v.title, title, StringComparison.OrdinalIgnoreCase))) title = baseTitle + " " + n++;
   var g = NewGroup(title);
   Edit(DeskChange.Groups, d => d.groups.Add(g));
   return g;
  }

  public void RenameGroup(string groupId, string title) {
   string trimmed = (title ?? "").Trim();
   if (trimmed.Length == 0) throw new InvalidDataException("文件夹名称不能为空。");
   Edit(DeskChange.Groups, d => { FindGroup(d, groupId).title = trimmed; });
  }

  /// Optimistic update: fails if title/subtitle changed since the caller read them.
  public void UpdateGroup(string groupId, string expectedTitle, string expectedSubtitle, string title, string subtitle) {
   Edit(DeskChange.Groups, d => {
    var current = FindGroup(d, groupId);
    if (current.title != expectedTitle || current.subtitle != expectedSubtitle) throw new InvalidDataException("分类名称已被其他操作修改，请重新编辑。");
    current.title = title;
    current.subtitle = subtitle;
   });
  }

  /// Removes the folder and its entries. Never touches the files the entries point to.
  public void DeleteGroup(string groupId) { Edit(DeskChange.Groups, d => d.groups.RemoveAll(v => v.id == groupId)); }

  /// Moves a folder in the list order; targetIndex is an insertion index in the current list
  /// (-1 = end).
  public void MoveGroup(string groupId, int targetIndex) {
   Edit(DeskChange.Groups, d => {
    int old = d.groups.FindIndex(v => v.id == groupId);
    if (old < 0) throw new InvalidDataException("分类已删除。");
    int index = targetIndex < 0 ? d.groups.Count : Math.Min(d.groups.Count, Math.Max(0, targetIndex));
    var g = d.groups[old];
    if (old < index) index--;
    d.groups.RemoveAt(old);
    d.groups.Insert(Math.Min(index, d.groups.Count), g);
   });
  }

  /// One-time migration to order-only layout: folders that were pinned at desktop positions
  /// are re-ordered in reading order (top-to-bottom, then left-to-right) ahead of unpinned
  /// ones (which keep their relative order), and all pins are cleared. Returns true if it
  /// changed anything. The desktop UI lays folders out purely by list order.
  public bool MigratePinnedPositionsToOrder() {
   if (!Data.groups.Exists(g => g.desktopX >= 0)) return false;
   Edit(DeskChange.Groups, d => {
    var indexed = new List<KeyValuePair<int, DeskGroup>>();
    for (int i = 0; i < d.groups.Count; i++) indexed.Add(new KeyValuePair<int, DeskGroup>(i, d.groups[i]));
    indexed.Sort((a, b) => {
     bool pa = a.Value.desktopX >= 0, pb = b.Value.desktopX >= 0;
     if (pa != pb) return pa ? -1 : 1;
     if (pa) {
      // Same row if the tops are within half a tile; then left to right.
      int dy = a.Value.desktopY - b.Value.desktopY;
      if (Math.Abs(dy) > 70) return dy;
      int dx = a.Value.desktopX - b.Value.desktopX;
      if (dx != 0) return dx;
     }
     return a.Key - b.Key;
    });
    d.groups.Clear();
    foreach (var pair in indexed) { pair.Value.desktopX = -1; pair.Value.desktopY = -1; d.groups.Add(pair.Value); }
   });
   return true;
  }

  // ---- Entries (items) -----------------------------------------------------------------

  /// Adds files/folders/shortcuts by path (never moves or copies them). index -1 = end.
  public void AddPaths(string groupId, IEnumerable<string> paths, int index) {
   var entries = new List<DeskItem>();
   foreach (string path in paths) entries.Add(EntryServices.FromPath(path));
   Edit(DeskChange.Groups, d => { var items = FindGroup(d, groupId).items; items.InsertRange(Clamp(index, items.Count), entries); });
  }

  /// Adds an http/https link. index -1 = end.
  public void AddUrl(string groupId, string url, int index) {
   var item = EntryServices.FromUrl(url);
   Edit(DeskChange.Groups, d => { var items = FindGroup(d, groupId).items; items.Insert(Clamp(index, items.Count), item); });
  }

  /// Adds a fully-formed item (e.g. from the edit dialog) at the end.
  public void AddItem(string groupId, DeskItem item) {
   if (item == null) throw new ArgumentNullException("item");
   var entry = item.CopyForEdit();
   EntryServices.NormalizeAppShortcut(entry);
   Edit(DeskChange.Groups, d => FindGroup(d, groupId).items.Add(entry));
  }

  /// Optimistic replace: fails if the stored item differs from `expected` (what the caller edited).
  public void UpdateItem(string groupId, string itemId, DeskItem expected, DeskItem edited) {
   string baseline = Snapshot(expected);
   Edit(DeskChange.Groups, d => {
    var items = FindGroup(d, groupId).items;
    int i = items.FindIndex(v => v.id == itemId);
    if (i < 0) throw new InvalidDataException("条目已删除。");
    if (Snapshot(items[i]) != baseline) throw new InvalidDataException("此入口已被其他操作修改，请重新编辑。");
    var entry = edited.CopyForEdit();
    EntryServices.NormalizeAppShortcut(entry);
    items[i] = entry;
   });
  }
  static string Snapshot(DeskItem item) { return new JavaScriptSerializer().Serialize(new[] { item.id, item.title, item.description, item.type, item.target, item.icon, item.arguments }); }

  public void RenameItem(string groupId, string itemId, string title) {
   string trimmed = (title ?? "").Trim();
   if (trimmed.Length == 0) throw new InvalidDataException("名称不能为空。");
   Edit(DeskChange.Groups, d => {
    var items = FindGroup(d, groupId).items;
    int i = items.FindIndex(v => v.id == itemId);
    if (i < 0) throw new InvalidDataException("条目已删除。");
    items[i].title = trimmed;
   });
  }

  /// Removes the entry only (the target file is never deleted).
  public void DeleteItem(string groupId, string itemId) { Edit(DeskChange.Groups, d => FindGroup(d, groupId).items.RemoveAll(v => v.id == itemId)); }

  /// Moves an entry within or between folders. insertIndex is an insertion index in the
  /// target list as it is *before* the move (-1 = end).
  public void MoveItem(string sourceGroupId, string itemId, string targetGroupId, int insertIndex) {
   Edit(DeskChange.Groups, d => {
    var source = FindGroup(d, sourceGroupId).items;
    var target = FindGroup(d, targetGroupId).items;
    int old = source.FindIndex(v => v.id == itemId);
    if (old < 0) throw new InvalidDataException("条目已删除。");
    var item = source[old];
    int index = Clamp(insertIndex, target.Count);
    if (source == target && old < index) index--;
    source.RemoveAt(old);
    target.Insert(Math.Min(index, target.Count), item);
   });
  }

  /// App-picker toggle: adds a copy of `candidate` (new id) if no entry has the same target,
  /// otherwise removes that entry. Returns true when the candidate is now in the folder.
  public bool ToggleCandidate(string groupId, DeskItem candidate) {
   if (candidate == null) throw new ArgumentNullException("candidate");
   bool added = false;
   Edit(DeskChange.Groups, d => {
    var items = FindGroup(d, groupId).items;
    int existing = items.FindIndex(v => String.Equals(v.target, candidate.target, StringComparison.OrdinalIgnoreCase));
    if (existing >= 0) { items.RemoveAt(existing); added = false; }
    else {
     EntryServices.ValidateTarget(candidate.type, candidate.target, true);
     items.Add(new DeskItem { id = DesktopStore.NewId(), title = candidate.title, description = candidate.description ?? "", type = candidate.type, target = candidate.target, icon = candidate.icon ?? "", arguments = candidate.arguments ?? "" });
     added = true;
    }
   });
   return added;
  }

  static int Clamp(int index, int count) { return index < 0 ? count : Math.Min(count, Math.Max(0, index)); }

  // ---- Calendar notes & preferences ----------------------------------------------------

  /// dateKey is "yyyy-MM-dd". Empty text removes the note. Line breaks become spaces.
  public void SetNote(string dateKey, string text) {
   if (!DesktopStore.IsDateKey(dateKey)) throw new InvalidDataException("日期格式无效。");
   string trimmed = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
   if ((GetNote(dateKey) ?? "") == trimmed) return;
   Edit(DeskChange.Notes, d => {
    if (d.notes == null) d.notes = new Dictionary<string, string>();
    if (trimmed.Length == 0) d.notes.Remove(dateKey); else d.notes[dateKey] = trimmed;
   });
  }
  public string GetNote(string dateKey) {
   string note = null;
   if (Data.notes != null && dateKey != null) Data.notes.TryGetValue(dateKey, out note);
   return note;
  }

  public void SetHomePosition(int x, int y) { Edit(DeskChange.Prefs, d => { d.homeX = x; d.homeY = y; }); }
  public void ToggleShowLabels() { Edit(DeskChange.Prefs, d => { d.showLabels = !d.showLabels; }); }

  // ---- Reminders (todos) ---------------------------------------------------------------
  // Dates are "yyyy-MM-dd" keys ("" = no date). Which day is "today" is the caller's choice;
  // see TodoQueries for the views built from these.

  /// Null when the reminder no longer exists.
  public DeskTodo FindTodo(string todoId) {
   return Data.todos == null ? null : Data.todos.Find(v => v.id == todoId);
  }
  static DeskTodo TodoIn(DesktopData d, string todoId) {
   var t = d.todos.Find(v => v.id == todoId);
   if (t == null) throw new InvalidDataException("待办已删除。");
   return t;
  }
  static string TodoTitle(string title) {
   string trimmed = (title ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
   if (trimmed.Length == 0) throw new InvalidDataException("待办内容不能为空。");
   return trimmed;
  }
  static string TodoDue(string dateKey) {
   if (String.IsNullOrEmpty(dateKey)) return "";
   if (!DesktopStore.IsDateKey(dateKey)) throw new InvalidDataException("日期格式无效。");
   return dateKey;
  }
  static string Stamp() { return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); }

  /// Appends a reminder (dueDateKey "" or null = no date). Returns a copy of what was saved.
  public DeskTodo AddTodo(string title, string dueDateKey) {
   var todo = new DeskTodo { id = DesktopStore.NewId(), title = TodoTitle(title), due = TodoDue(dueDateKey), created = Stamp() };
   Edit(DeskChange.Todos, d => d.todos.Add(todo.CopyForEdit()));
   return todo;
  }

  public void RenameTodo(string todoId, string title) {
   string trimmed = TodoTitle(title);
   Edit(DeskChange.Todos, d => { TodoIn(d, todoId).title = trimmed; });
  }

  /// Marks a reminder done (stamping `completed`) or open again. No-op if already in that state.
  public void SetTodoDone(string todoId, bool done) {
   var current = FindTodo(todoId);
   if (current != null && current.done == done) return;
   string stamp = done ? Stamp() : "";
   Edit(DeskChange.Todos, d => { var t = TodoIn(d, todoId); t.done = done; t.completed = stamp; });
  }

  /// Several done / open changes in one save (the UI batches quick successive ticks). Reminders
  /// deleted in the meantime are skipped; entries already in the requested state are ignored.
  public void SetTodosDone(IDictionary<string, bool> states) {
   if (states == null) throw new ArgumentNullException("states");
   if (states.Count == 0) return;
   var requested = new Dictionary<string, bool>(states, StringComparer.Ordinal);
   if (!Data.todos.Exists(t => TodoStateChanges(t, requested))) return;
   string stamp = Stamp();
   Edit(DeskChange.Todos, d => {
    foreach (var t in d.todos) {
     bool done;
     if (!requested.TryGetValue(t.id, out done) || t.done == done) continue;
     t.done = done;
     t.completed = done ? stamp : "";
    }
   });
  }

  static bool TodoStateChanges(DeskTodo todo, IDictionary<string, bool> states) {
   bool done;
   return states.TryGetValue(todo.id, out done) && todo.done != done;
  }

  /// Moves a reminder to another day ("" or null = no date).
  public void SetTodoDue(string todoId, string dueDateKey) {
   string due = TodoDue(dueDateKey);
   Edit(DeskChange.Todos, d => { TodoIn(d, todoId).due = due; });
  }

  public void DeleteTodo(string todoId) { Edit(DeskChange.Todos, d => d.todos.RemoveAll(v => v.id == todoId)); }

  // ---- Side effects --------------------------------------------------------------------

  /// Opens an entry through the Shell (as the desktop would). Throws if the target is gone.
  public void Launch(string groupId, string itemId) {
   var item = FindItem(groupId, itemId);
   if (item == null) throw new InvalidDataException("条目已删除。");
   EntryServices.Open(item);
  }

  /// Starts an app entry with ShellExecute. Safe on a worker thread (resolving a shortcut or
  /// an elevation prompt can take a while); pass a copy of the item read on the UI thread.
  public static void OpenAppOnWorker(DeskItem item) {
   if (item == null) throw new ArgumentNullException("item");
   EntryServices.OpenDesktopApp(item);
  }

  /// Context-menu actions on an entry: terminal / editor at its folder, or reveal in Explorer.
  public void OpenWith(string groupId, string itemId, EntryAction action) {
   var item = FindItem(groupId, itemId);
   if (item == null) throw new InvalidDataException("条目已删除。");
   switch (action) {
    case EntryAction.Terminal: EntryServices.OpenInTerminal(item); break;
    case EntryAction.VSCode: EntryServices.OpenInCodeEditor(item, "vscode"); break;
    case EntryAction.Cursor: EntryServices.OpenInCodeEditor(item, "cursor"); break;
    default: EntryServices.OpenContainingFolder(item); break;
   }
  }

  /// Whether the editor behind EntryAction.VSCode / EntryAction.Cursor is installed.
  public static bool HasEditor(EntryAction editor) {
   if (editor == EntryAction.VSCode) return EntryServices.FindEditorExecutable("vscode") != null;
   if (editor == EntryAction.Cursor) return EntryServices.FindEditorExecutable("cursor") != null;
   return false;
  }

  /// Opens the user's desktop folder or MacDesk's backup folder in Explorer.
  public void OpenFolder(DataFolder folder) {
   string directory = folder == DataFolder.Backups ? Path.Combine(Root, "Backups") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
   Directory.CreateDirectory(directory);
   EntryServices.Open(new DeskItem { type = "folder", target = directory });
  }

  /// Installed apps from the Start menu / desktop (cached ~45 s). Items have fresh ids and are
  /// not part of any folder until passed to ToggleCandidate. Safe on a worker thread.
  public List<DeskItem> DiscoverApps() { return EntryServices.DiscoverSystemApps(); }

  /// 128 px icon for an entry (cached; never null). Safe on a worker thread.
  public Image GetIcon(DeskItem item) { return IconProvider.Get(item); }

  /// A new, uncached icon image that the caller owns and disposes. Safe on a worker thread.
  public static Image LoadIcon(DeskItem item) { return IconProvider.Load(item); }
 }
}
