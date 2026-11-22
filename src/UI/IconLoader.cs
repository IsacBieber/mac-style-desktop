using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MacDesk.Core;

namespace MacDeskNative {
 // The UI owns the cache. One worker at a time extracts a small batch of independent bitmaps.
 internal sealed class IconLoader : IDisposable {
  private const int BatchSize = 4;
  private readonly LinkedList<DeskItem> foreground = new LinkedList<DeskItem>();
  private readonly LinkedList<DeskItem> previews = new LinkedList<DeskItem>();
  private readonly Dictionary<string, LinkedListNode<DeskItem>> pending = new Dictionary<string, LinkedListNode<DeskItem>>();
  private readonly HashSet<string> inFlight = new HashSet<string>();
  private readonly Timer timer = new Timer { Interval = 12 };
  private readonly Func<bool> canPublish;
  private readonly Action<HashSet<string>> onReady;
  private Task<List<Result>> running;

  private sealed class Result {
   internal string Key;
   internal Bitmap Icon;
  }

  internal IconLoader(Func<bool> canPublish, Action<HashSet<string>> onReady) {
   this.canPublish = canPublish;
   this.onReady = onReady;
   timer.Tick += Poll;
  }

  internal Bitmap Get(DeskItem item, bool priority) {
   string key = FolderLayer.IconKey(item);
   Bitmap icon;
   if (FolderLayer.TryGetIcon(key, out icon) || inFlight.Contains(key)) return icon;
   LinkedListNode<DeskItem> request;
   if (pending.TryGetValue(key, out request)) {
    if (priority && request.List == previews) {
     previews.Remove(request);
     foreground.AddLast(request);
    }
    return null;
   }
   pending.Add(key, (priority ? foreground : previews).AddLast(item));
   if (running == null) StartBatch();
   timer.Start();
   return null;
  }

  private void StartBatch() {
   if (pending.Count == 0) return;
   var batch = new List<DeskItem>(BatchSize);
   while (batch.Count < BatchSize && pending.Count > 0) {
    var queue = foreground.Count > 0 ? foreground : previews;
    DeskItem item = queue.First.Value;
    queue.RemoveFirst();
    string key = FolderLayer.IconKey(item);
    pending.Remove(key);
    inFlight.Add(key);
    batch.Add(item);
   }
   running = Task.Factory.StartNew(() => {
    var results = new List<Result>(batch.Count);
    foreach (DeskItem item in batch)
     results.Add(new Result { Key = FolderLayer.IconKey(item), Icon = FolderLayer.LoadIcon(item) });
    return results;
   }, System.Threading.CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
  }

  private void Poll(object sender, EventArgs args) {
   if (running != null && running.IsCompleted && canPublish()) {
    foreach (var result in running.GetAwaiter().GetResult()) FolderLayer.StoreIcon(result.Key, result.Icon);
    var changed = new HashSet<string>(inFlight);
    inFlight.Clear();
    running = null;
    onReady(changed);
   }
   if (running == null) StartBatch();
   if (running == null) timer.Stop();
  }

  public void Dispose() {
   timer.Dispose();
   if (running != null)
    running.ContinueWith(task => {
     if (task.Status == TaskStatus.RanToCompletion) {
      foreach (var result in task.Result) if (result.Icon != null) result.Icon.Dispose();
     } else if (task.IsFaulted) GC.KeepAlive(task.Exception);
    }, TaskScheduler.Default);
   running = null;
   foreground.Clear();
   previews.Clear();
   pending.Clear();
   inFlight.Clear();
  }
 }
}
