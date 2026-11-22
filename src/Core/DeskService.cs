using System;
using System.Collections.Generic;
using System.Drawing;
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
 //  Threading: call from one thread (the UI thread). Changed fires synchronously after save.
 // ============================================================================================
 public sealed class DeskService {
  readonly DesktopStore store;

  public DeskService(string root) { store = new DesktopStore(root); }

  /// Folder holding desktop.json and Backups\.
  public string Root { get { return store.Root; } }
  /// Current data. Treat as read-only; it is replaced (not mutated) on every successful change.
  public DesktopData Data { get { return store.Data; } }
  /// Raised after every successful change (and after Reload).
  public event EventHandler Changed;

  public void Reload() { store.Reload(); OnChanged(); }

  void Edit(Action<DesktopData> mutation) { store.Edit(mutation); OnChanged(); }
  void OnChanged() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }

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
    Edit(d => { if (d.groups.Count > 0) return; d.groups.Add(dev); d.groups.Add(daily); d.groups.Add(games); d.groups.Add(tools); });
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
   Edit(d => d.groups.Add(group));
  }

  /// Creates an empty folder titled baseTitle, or "baseTitle 2/3/..." if taken. Returns it.
  public DeskGroup CreateGroupWithUniqueTitle(string baseTitle) {
   string title = baseTitle;
   int n = 2;
   while (Data.groups.Exists(v => String.Equals(v.title, title, StringComparison.OrdinalIgnoreCase))) title = baseTitle + " " + n++;
   var g = NewGroup(title);
   Edit(d => d.groups.Add(g));
   return g;
  }

  public void RenameGroup(string groupId, string title) {
   string trimmed = (title ?? "").Trim();
   if (trimmed.Length == 0) throw new InvalidDataException("文件夹名称不能为空。");
   Edit(d => { FindGroup(d, groupId).title = trimmed; });
  }

  /// Optimistic update: fails if title/subtitle changed since the caller read them.
  public void UpdateGroup(string groupId, string expectedTitle, string expectedSubtitle, string title, string subtitle) {
   Edit(d => {
    var current = FindGroup(d, groupId);
    if (current.title != expectedTitle || current.subtitle != expectedSubtitle) throw new InvalidDataException("分类名称已被其他操作修改，请重新编辑。");
    current.title = title;
    current.subtitle = subtitle;
   });
  }

  /// Removes the folder and its entries. Never touches the files the entries point to.
  public void DeleteGroup(string groupId) { Edit(d => d.groups.RemoveAll(v => v.id == groupId)); }

  /// Moves a folder in the list order; targetIndex is an insertion index in the current list
  /// (-1 = end).
  public void MoveGroup(string groupId, int targetIndex) {
   Edit(d => {
    int old = d.groups.FindIndex(v => v.id == groupId);
    if (old < 0) throw new InvalidDataException("分类已删除。");
    int index = targetIndex < 0 ? d.groups.Count : Math.Min(d.groups.Count, Math.Max(0, targetIndex));
    var g = d.groups[old];
    if (old < index) index--;
    d.groups.RemoveAt(old);
    d.groups.Insert(Math.Min(index, d.groups.Count), g);
   });
  }

  /// Pins a folder at a desktop position (clamped into `area`). Folders that are still
  /// auto-placed get pinned at their current layout positions so nothing jumps.
  public void SetGroupPosition(string groupId, int x, int y, IDictionary<string, Point> currentLayout, Size area) {
   Edit(d => {
    if (currentLayout != null)
     foreach (var group in d.groups) { Point p; if (group.desktopX < 0 && currentLayout.TryGetValue(group.id, out p)) { group.desktopX = p.X; group.desktopY = p.Y; } }
    var g = FindGroup(d, groupId);
    g.desktopX = Math.Max(24, Math.Min(Math.Max(24, area.Width - 112), x));
    g.desktopY = Math.Max(24, Math.Min(Math.Max(24, area.Height - 154), y));
   });
  }

  /// Pins several folders at once (e.g. the dragged folder plus the ones that made room for
  /// it), as one atomic save. Same clamping and auto-placed pinning as SetGroupPosition.
  public void SetGroupPositions(IDictionary<string, Point> positions, IDictionary<string, Point> currentLayout, Size area) {
   if (positions == null) throw new ArgumentNullException("positions");
   Edit(d => {
    if (currentLayout != null)
     foreach (var group in d.groups) { Point p; if (group.desktopX < 0 && currentLayout.TryGetValue(group.id, out p)) { group.desktopX = p.X; group.desktopY = p.Y; } }
    foreach (var pair in positions) {
     var g = FindGroup(d, pair.Key);
     g.desktopX = Math.Max(24, Math.Min(Math.Max(24, area.Width - 112), pair.Value.X));
     g.desktopY = Math.Max(24, Math.Min(Math.Max(24, area.Height - 154), pair.Value.Y));
    }
   });
  }

  /// One-time migration to order-only layout: folders that were pinned at desktop positions
  /// are re-ordered in reading order (top-to-bottom, then left-to-right) ahead of unpinned
  /// ones (which keep their relative order), and all pins are cleared. Returns true if it
  /// changed anything. The desktop UI lays folders out purely by list order.
  public bool MigratePinnedPositionsToOrder() {
   if (!Data.groups.Exists(g => g.desktopX >= 0)) return false;
   Edit(d => {
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

  /// Returns every folder to automatic grid placement.
  public void ResetGroupPositions() { Edit(d => { foreach (var g in d.groups) { g.desktopX = -1; g.desktopY = -1; } }); }

  // ---- Entries (items) -----------------------------------------------------------------

  /// Adds files/folders/shortcuts by path (never moves or copies them). index -1 = end.
  public void AddPaths(string groupId, IEnumerable<string> paths, int index) {
   var entries = new List<DeskItem>();
   foreach (string path in paths) entries.Add(EntryServices.FromPath(path));
   Edit(d => { var items = FindGroup(d, groupId).items; items.InsertRange(Clamp(index, items.Count), entries); });
  }

  /// Adds an http/https link. index -1 = end.
  public void AddUrl(string groupId, string url, int index) {
   var item = EntryServices.FromUrl(url);
   Edit(d => { var items = FindGroup(d, groupId).items; items.Insert(Clamp(index, items.Count), item); });
  }

  /// Adds a fully-formed item (e.g. from the edit dialog) at the end.
  public void AddItem(string groupId, DeskItem item) {
   if (item == null) throw new ArgumentNullException("item");
   var entry = item.CopyForEdit();
   EntryServices.NormalizeAppShortcut(entry);
   Edit(d => FindGroup(d, groupId).items.Add(entry));
  }

  /// Optimistic replace: fails if the stored item differs from `expected` (what the caller edited).
  public void UpdateItem(string groupId, string itemId, DeskItem expected, DeskItem edited) {
   string baseline = Snapshot(expected);
   Edit(d => {
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
   Edit(d => {
    var items = FindGroup(d, groupId).items;
    int i = items.FindIndex(v => v.id == itemId);
    if (i < 0) throw new InvalidDataException("条目已删除。");
    items[i].title = trimmed;
   });
  }

  /// Removes the entry only (the target file is never deleted).
  public void DeleteItem(string groupId, string itemId) { Edit(d => FindGroup(d, groupId).items.RemoveAll(v => v.id == itemId)); }

  /// Moves an entry within or between folders. insertIndex is an insertion index in the
  /// target list as it is *before* the move (-1 = end).
  public void MoveItem(string sourceGroupId, string itemId, string targetGroupId, int insertIndex) {
   Edit(d => {
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
   Edit(d => {
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
   string trimmed = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
   Edit(d => {
    if (d.notes == null) d.notes = new Dictionary<string, string>();
    if (trimmed.Length == 0) d.notes.Remove(dateKey); else d.notes[dateKey] = trimmed;
   });
  }
  public string GetNote(string dateKey) {
   string note = null;
   if (Data.notes != null) Data.notes.TryGetValue(dateKey, out note);
   return note;
  }

  public void SetHomePosition(int x, int y) { Edit(d => { d.homeX = x; d.homeY = y; }); }
  public void ToggleShowLabels() { Edit(d => { d.showLabels = !d.showLabels; }); }

  // ---- Side effects --------------------------------------------------------------------

  /// Opens an entry through the Shell (as the desktop would). Throws if the target is gone.
  public void Launch(string groupId, string itemId) {
   var item = FindItem(groupId, itemId);
   if (item == null) throw new InvalidDataException("条目已删除。");
   EntryServices.Open(item);
  }

  /// Installed apps from the Start menu / desktop (cached ~45 s). Items have fresh ids and are
  /// not part of any folder until passed to ToggleCandidate.
  public List<DeskItem> DiscoverApps() { return EntryServices.DiscoverSystemApps(); }

  /// 128 px icon for an entry (cached; never null).
  public Image GetIcon(DeskItem item) { return IconProvider.Get(item); }
 }
}
