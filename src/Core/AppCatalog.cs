using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace MacDesk.Core {
 // The picker reads snapshots on the UI thread. All cache and discovery I/O runs on one worker.
 internal sealed class AppCatalog {
  readonly string path;
  readonly Stopwatch age = new Stopwatch();
  bool refreshing;

  internal IReadOnlyList<DeskItem> Items { get; private set; }
  internal bool Loading { get { return refreshing && Items == null; } }

  internal AppCatalog(string root) { path = Path.Combine(root, "app-catalog-v1.json"); }

  internal void Refresh(Action<Action> dispatch, Action changed) {
   if (refreshing || (age.IsRunning && age.Elapsed.TotalSeconds < 45)) return;
   bool readCache = Items == null;
   refreshing = true;
   age.Restart();
   ThreadPool.QueueUserWorkItem(delegate {
    List<DeskItem> cached = readCache ? ReadCache() : null;
    if (cached != null) Post(dispatch, delegate { Publish(cached); changed(); });
    List<DeskItem> fresh = null;
    try {
     fresh = Scan();
     if (fresh.Count != 0) Persist(fresh);
    } catch (Exception ex) { Debug.WriteLine(ex); }
    Post(dispatch, delegate {
     refreshing = false;
     if (fresh != null && (fresh.Count != 0 || Items == null)) {
      if (SameEntries(fresh)) return;
      Publish(fresh);
     }
     changed();
    });
   });
  }

  static List<DeskItem> Scan() {
   var items = EntryServices.ScanSystemApps(int.MaxValue);
   foreach (var item in items) item.id = item.target;
   items.Sort((a, b) => {
    int byTitle = StringComparer.CurrentCultureIgnoreCase.Compare(a.title, b.title);
    return byTitle != 0 ? byTitle : StringComparer.OrdinalIgnoreCase.Compare(a.target, b.target);
   });
   return items;
  }

  internal void LoadForBenchmark() { Publish(Scan()); age.Restart(); }

  static void Post(Action<Action> dispatch, Action action) {
   try { dispatch(action); }
   catch (InvalidOperationException) { }
  }

  void Publish(List<DeskItem> items) { Items = items.AsReadOnly(); }

  bool SameEntries(List<DeskItem> next) {
   if (Items == null || Items.Count != next.Count) return false;
   for (int i = 0; i < next.Count; i++)
    if (Items[i].target != next[i].target || Items[i].title != next[i].title) return false;
   return true;
  }

  List<DeskItem> ReadCache() {
   try {
    var cached = new JavaScriptSerializer().Deserialize<List<DeskItem>>(File.ReadAllText(path));
    if (cached == null) return null;
    cached.RemoveAll(item => item == null || item.type != "app" || !EntryServices.IsAbsolutePath(item.target));
    foreach (var item in cached) item.id = item.target;
    return cached;
   } catch (Exception ex) { Debug.WriteLine(ex); return null; }
  }

  void Persist(List<DeskItem> items) {
   string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
   try {
    string json = new JavaScriptSerializer().Serialize(items);
    if (File.Exists(path) && File.ReadAllText(path) == json) return;
    File.WriteAllText(temp, json);
    if (File.Exists(path)) File.Replace(temp, path, null);
    else File.Move(temp, path);
   } finally { if (File.Exists(temp)) File.Delete(temp); }
  }
 }
}
