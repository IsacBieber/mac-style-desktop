using System;
using System.Drawing;
using System.IO;

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

   svc.SetGroupPosition("two", 500, 300, null, new Size(1920, 1080));
   Check(svc.GetGroup("two").desktopX == 500 && svc.GetGroup("two").desktopY == 300, "pin folder position");
   svc.ResetGroupPositions();
   Check(svc.GetGroup("two").desktopX == -1, "reset positions");
   // Pinned layout -> order. List is [two, one]; pin "one" left of "two" on the same row.
   svc.SetGroupPosition("one", 300, 30, null, new Size(1920, 1080));
   svc.SetGroupPosition("two", 700, 24, null, new Size(1920, 1080));
   Check(svc.MigratePinnedPositionsToOrder() && svc.Data.groups[0].id == "one" && svc.Data.groups[1].id == "two" && svc.Data.groups[0].desktopX == -1, "pinned positions migrate to reading order");
   Check(!svc.MigratePinnedPositionsToOrder(), "migration runs once");
   svc.MoveGroup("two", 0); // restore [two, one] for the checks below
   int before = changes;
   var batch = new System.Collections.Generic.Dictionary<string, Point> { { "one", new Point(700, 24) }, { "two", new Point(832, 24) } };
   svc.SetGroupPositions(batch, null, new Size(1920, 1080));
   Check(changes == before + 1 && svc.GetGroup("one").desktopX == 700 && svc.GetGroup("two").desktopX == 832, "batch positions save atomically");
   svc.ResetGroupPositions();

   var fresh = new DesktopStore(root);
   Check(fresh.Data.groups[0].items[0].id == first, "restart persistence");
   Check(fresh.Data.notes["2026-11-22"] == "里程碑检查" && fresh.Data.showLabels, "notes and labels persistence");
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
  }
 }
}
