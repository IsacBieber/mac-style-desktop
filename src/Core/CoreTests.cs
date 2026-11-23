using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace MacDesk.Core {
 // Backend self-tests (run by `MacDesk.exe --self-test`). Exercise DeskService against a
 // throw-away root; no window is created. Keep these passing when changing the backend.
 public static class CoreTests {
  static void Check(bool value, string name) { if (!value) throw new Exception("Self test failed: " + name); }

  public static void Run(string root) {
   Directory.CreateDirectory(root);
   string file = Path.Combine(root, "原文件.txt");
   File.WriteAllText(file, "UNCHANGED");
   var svc = new DeskService(root);
   int changes = 0;
   svc.Changed += delegate { changes++; };

   svc.AddGroup(new DeskGroup { id = "one", title = "分类一", subtitle = "" });
   svc.AddGroup(new DeskGroup { id = "two", title = "分类二", subtitle = "" });
   svc.AddPaths("one", new[] { file }, -1);
   string first = svc.Data.groups[0].items[0].id;
   svc.AddUrl("one", "https://example.com/", -1);
   string second = svc.Data.groups[0].items[1].id;
   Check(changes == 4, "Changed fires once per successful edit");

   svc.RenameItem("one", first, "重命名文件");
   Check(svc.Data.groups[0].items[0].title == "重命名文件", "rename item");
   svc.RenameGroup("one", "核心开发");
   Check(svc.Data.groups[0].title == "核心开发", "rename group");
   bool threw = false;
   try { svc.RenameGroup("one", "   "); } catch (InvalidDataException) { threw = true; }
   Check(threw && svc.Data.groups[0].title == "核心开发", "empty rename rejected without change");

   var cand = new DeskItem { title = "测试工具", type = "url", target = "https://example.org/tool" };
   Check(svc.ToggleCandidate("one", cand) && svc.Data.groups[0].items.Count == 3, "toggle candidate add");
   Check(!svc.ToggleCandidate("one", cand) && svc.Data.groups[0].items.Count == 2, "toggle candidate remove");
   Check(svc.DiscoverApps() != null, "discover apps");

   svc.SetNote("2026-11-21", "发布 MacDesk 2.0");
   Check(svc.GetNote("2026-11-21") == "发布 MacDesk 2.0", "note save");
   svc.SetNote("2026-11-21", "");
   Check(svc.GetNote("2026-11-21") == null, "note clear");
   svc.SetNote("2026-11-22", "里程碑检查");
   svc.ToggleShowLabels();
   Check(svc.Data.showLabels, "toggle labels");

   svc.MoveItem("one", first, "one", 2);
   Check(svc.Data.groups[0].items[0].id == second, "same-folder insert gap");
   svc.MoveItem("one", first, "two", -1);
   Check(svc.Data.groups[0].items.Count == 1 && svc.Data.groups[1].items[0].id == first, "cross-folder move");
   svc.MoveGroup("one", 2);
   Check(svc.Data.groups[1].id == "one", "folder reorder");

   var edited = new DeskItem { id = first, title = "新名字", description = "", type = "file", target = file, icon = "", arguments = "" };
   svc.UpdateItem("two", first, svc.FindItem("two", first), edited);
   Check(svc.FindItem("two", first).title == "新名字", "update item");
   threw = false;
   try { svc.UpdateItem("two", first, new DeskItem { id = first, title = "stale" }, edited); } catch (InvalidDataException) { threw = true; }
   Check(threw, "stale update rejected");

   // Pinned layout written by an older version -> order. List is [two, one]; "one" sits left of "two" on the same row.
   new DesktopStore(root).Edit(d => {
    var a = d.groups.Find(g => g.id == "one"); a.desktopX = 300; a.desktopY = 30;
    var b = d.groups.Find(g => g.id == "two"); b.desktopX = 700; b.desktopY = 24;
   });
   svc.Reload();
   Check(svc.MigratePinnedPositionsToOrder() && svc.Data.groups[0].id == "one" && svc.Data.groups[1].id == "two" && svc.Data.groups[0].desktopX == -1, "pinned positions migrate to reading order");
   Check(!svc.MigratePinnedPositionsToOrder(), "migration runs once");
   svc.MoveGroup("two", 0); // restore [two, one] for the checks below

   CheckTodos(svc);

   var fresh = new DesktopStore(root);
   Check(fresh.Data.groups[0].items[0].id == first, "restart persistence");
   Check(fresh.Data.notes["2026-11-22"] == "里程碑检查" && fresh.Data.showLabels, "notes and labels persistence");
   Check(fresh.Data.version == DesktopData.CurrentVersion && fresh.Data.todos.Count == 4, "reminders persistence");
   Check(File.ReadAllText(file) == "UNCHANGED", "never touches source files");
   var old = fresh.Data.groups[0].title;
   try { fresh.Edit(d => { d.groups[0].title = ""; }); throw new Exception("Invalid title accepted"); } catch (InvalidDataException) { }
   Check(fresh.Data.groups[0].title == old, "validation rollback");
   var beforeFailure = fresh.Data;
   try {
    fresh.Edit(d => { d.groups[0].items[0].title = "discard"; d.notes["bad-date"] = "x"; });
    throw new Exception("Invalid date key accepted");
   } catch (InvalidDataException) { }
   Check(fresh.Data == beforeFailure && fresh.Data.groups[0].items[0].title == "新名字" &&
    !fresh.Data.notes.ContainsKey("bad-date"), "failed edit cannot modify published items or notes");
   fresh.Edit(d => { d.groups[0].items[0].title = "保留"; d.notes["2026-11-22"] = "已更新"; });
   Check(beforeFailure.groups[0].items[0].title == "新名字" && beforeFailure.notes["2026-11-22"] == "里程碑检查",
    "successful edit leaves older snapshots intact");

   string seedRoot = Path.Combine(root, "seed");
   Directory.CreateDirectory(seedRoot);
   var seeded = new DeskService(seedRoot);
   Check(seeded.SeedStarterGroupsIfNew() && seeded.Data.groups.Count == 4 && File.Exists(Path.Combine(seedRoot, "desktop.json")), "seed starter folders");
   Check(!seeded.SeedStarterGroupsIfNew(), "seed only once");
   CheckShortcutTypes(Path.Combine(root, "shortcut-types"));
   CheckVersionMigration(Path.Combine(root, "v1"));
   CheckBackupRetention(Path.Combine(root, "prune"));
  }

  static void CheckTodos(DeskService svc) {
   DeskChange last = DeskChange.None;
   int saves = 0;
   svc.Changed += delegate(object s, EventArgs e) { last = DeskChangedEventArgs.KindsOf(e); saves++; };
   svc.RenameGroup("one", "核心开发");
   Check(last == DeskChange.Groups, "folder edits report Groups");
   svc.SetNote("2026-11-24", "评审");
   Check(last == DeskChange.Notes, "note edits report Notes");
   int before = saves;
   svc.SetNote("2026-11-24", " 评审 ");
   Check(saves == before, "unchanged note is not saved again");
   svc.SetNote("2026-11-24", "");
   svc.ToggleShowLabels(); svc.ToggleShowLabels();
   Check(last == DeskChange.Prefs, "preference edits report Prefs");

   var t1 = svc.AddTodo("  写周报 ", "2026-11-20");
   Check(t1.title == "写周报" && last == DeskChange.Todos && t1.created.Length > 0, "add reminder trims, stamps and reports Todos");
   var t2 = svc.AddTodo("发布检查", "2026-11-23");
   var t3 = svc.AddTodo("买咖啡豆", "");
   var t4 = svc.AddTodo("更早的逾期", "2026-11-18");
   var agenda = TodoQueries.Agenda(svc.Data, "2026-11-23");
   Check(agenda.Count == 4 && agenda[0].Todo.id == t4.id && agenda[1].Todo.id == t1.id && agenda[0].Section == TodoQueries.AgendaSection.Overdue &&
    agenda[2].Todo.id == t2.id && agenda[2].Section == TodoQueries.AgendaSection.Today &&
    agenda[3].Todo.id == t3.id && agenda[3].Section == TodoQueries.AgendaSection.Undated, "agenda: overdue oldest first, then today, then undated");

   svc.SetTodoDone(t2.id, true);
   Check(svc.FindTodo(t2.id).done && svc.FindTodo(t2.id).completed.Length > 0, "completing a reminder stamps it");
   before = saves;
   svc.SetTodoDone(t2.id, true);
   Check(saves == before, "completing twice does not save again");
   Check(TodoQueries.Agenda(svc.Data, "2026-11-23").Count == 3, "completed reminders leave the agenda");
   var t5 = svc.AddTodo("同日第二项", "2026-11-23");
   var day = TodoQueries.ForDay(svc.Data, "2026-11-23");
   Check(day.Count == 2 && day[0].id == t5.id && day[1].id == t2.id, "day list puts completed reminders last");
   svc.SetTodoDone(t2.id, false);
   Check(!svc.FindTodo(t2.id).done && svc.FindTodo(t2.id).completed == "", "reopening clears the stamp");

   int openCount;
   var preview = TodoQueries.AgendaPreview(svc.Data, "2026-11-23", t => !t.done, 4, out openCount);
   Check(openCount == 5 && preview.Count == 4 && preview[0].Todo.id == t4.id && preview[1].Todo.id == t1.id &&
    preview[2].Todo.id == t2.id && preview[3].Todo.id == t5.id, "widget preview keeps agenda priority and stable order under a row limit");

   before = saves;
   svc.SetTodosDone(new System.Collections.Generic.Dictionary<string, bool> { { t1.id, true }, { t5.id, true }, { "gone", true } });
   Check(saves == before + 1 && svc.FindTodo(t1.id).done && svc.FindTodo(t5.id).done, "batched ticks save once and skip deleted reminders");
   svc.SetTodosDone(new System.Collections.Generic.Dictionary<string, bool> { { t1.id, false }, { t5.id, false } });

   svc.RenameTodo(t3.id, "买豆子");
   svc.SetTodoDue(t3.id, "2026-11-25");
   Check(svc.FindTodo(t3.id).title == "买豆子" && svc.FindTodo(t3.id).due == "2026-11-25", "rename and reschedule");
   var marks = TodoQueries.DayMarkers(svc.Data, 2026, 11);
   Check((marks[25] & TodoQueries.DayMark.OpenTodos) != 0 && (marks[22] & TodoQueries.DayMark.Note) != 0 &&
    marks[24] == TodoQueries.DayMark.None && (marks[20] & TodoQueries.DayMark.OpenTodos) != 0, "calendar day markers");
   svc.SetTodoDue(t3.id, "");
   Check(svc.FindTodo(t3.id).due == "", "remove a reminder's date");

   var snapshot = svc.Data;
   foreach (var bad in new[] { new[] { "   ", "" }, new[] { "x", "2026-02-30" }, new[] { "x", "11/23" }, new[] { new string('长', DesktopStore.MaxTodoTitle + 1), "" } }) {
    bool rejected = false;
    try { svc.AddTodo(bad[0], bad[1]); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && svc.Data == snapshot, "invalid reminder rejected without change");
   }
   bool missing = false;
   try { svc.RenameTodo("missing", "x"); } catch (InvalidDataException) { missing = true; }
   Check(missing, "editing a deleted reminder fails");
   svc.DeleteTodo(t4.id);
   Check(svc.FindTodo(t4.id) == null && svc.Data.todos.Count == 4, "delete reminder");
  }

  static void CheckVersionMigration(string root) {
   Directory.CreateDirectory(root);
   string path = Path.Combine(root, "desktop.json");
   File.WriteAllText(path, "{\"version\":1,\"groups\":[{\"id\":\"g1\",\"title\":\"旧文件夹\",\"subtitle\":\"\",\"items\":[],\"desktopX\":-1,\"desktopY\":-1}],\"notes\":{\"2026-01-02\":\"旧备忘\"},\"showLabels\":false,\"homeX\":-1,\"homeY\":-1}", new System.Text.UTF8Encoding(false));
   var upgraded = new DeskService(root);
   Check(upgraded.Data.version == 2 && upgraded.Data.todos.Count == 0 && upgraded.GetNote("2026-01-02") == "旧备忘" && upgraded.Data.groups[0].title == "旧文件夹", "version 1 file loads as version 2");
   upgraded.AddTodo("新待办", "");
   Check(File.ReadAllText(path).Contains("\"version\":2") && new DesktopStore(root).Data.todos.Count == 1, "first save writes version 2");
   File.WriteAllText(path, "{\"version\":3,\"groups\":[]}");
   bool refused = false;
   try { new DesktopStore(root); } catch (InvalidDataException) { refused = true; }
   Check(refused, "a newer data version is refused, not rewritten");
  }

  static void CheckBackupRetention(string dir) {
   Directory.CreateDirectory(dir);
   var now = new DateTime(2026, 11, 23, 12, 0, 0);
   for (int i = 0; i < 60; i++) File.WriteAllText(Path.Combine(dir, "data-20261123-" + (100000 + i) + "-0000000-x.json"), "");
   for (int d = 1; d <= 40; d++)
    for (int k = 0; k < 3; k++) File.WriteAllText(Path.Combine(dir, "data-" + now.AddDays(-d).ToString("yyyyMMdd") + "-12000" + k + "-0000000-x.json"), "");
   File.WriteAllText(Path.Combine(dir, "readme.txt"), "keep");
   DesktopStore.PruneBackups(dir, now);
   Func<int, int, string> past = (d, k) => Path.Combine(dir, "data-" + now.AddDays(-d).ToString("yyyyMMdd") + "-12000" + k + "-0000000-x.json");
   Check(Directory.GetFiles(dir, "data-*.json").Length == DesktopStore.KeepRecentBackups + DesktopStore.KeepDailyBackupDays - 1, "backup retention count");
   Check(File.Exists(Path.Combine(dir, "data-20261123-100059-0000000-x.json")) && !File.Exists(Path.Combine(dir, "data-20261123-100000-0000000-x.json")) &&
    File.Exists(past(1, 2)) && !File.Exists(past(1, 1)) && File.Exists(past(29, 2)) && !File.Exists(past(30, 2)) && File.Exists(Path.Combine(dir, "readme.txt")),
    "backup retention keeps the newest, then one per recent day");
  }

  static void CheckShortcutTypes(string root) {
   Directory.CreateDirectory(root);
   string directory = Directory.CreateDirectory(Path.Combine(root, "target folder")).FullName;
   string document = Path.Combine(root, "document.txt");
   File.WriteAllText(document, "test");
   string[] targets = { directory, document, Path.Combine(Environment.SystemDirectory, "cmd.exe") };
   string[] types = { "file", "file", "app" };
   var group = new DeskGroup { id = "legacy", title = "Legacy shortcuts" };
   object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
   try {
    for (int i = 0; i < targets.Length; i++) {
     string path = Path.Combine(root, i + ".lnk");
     object shortcut = ((dynamic)shell).CreateShortcut(path);
     try {
      ((dynamic)shortcut).TargetPath = targets[i];
      ((dynamic)shortcut).Save();
     } finally {
      Marshal.ReleaseComObject(shortcut);
     }
     DeskItem item = EntryServices.FromPath(path);
     Check(item.type == types[i] && item.target == path, "shortcut classification preserves its launch target");
     item.type = "app"; // Older imports marked every .lnk as an app.
     group.items.Add(item);
    }
   } finally {
    Marshal.ReleaseComObject(shell);
   }
   var store = new DesktopStore(root);
   store.Edit(data => data.groups.Add(group));
   store.Reload();
   for (int i = 0; i < targets.Length; i++)
    Check(store.Data.groups[0].items[i].type == types[i], "legacy shortcut classification on load");
  }
 }
}
