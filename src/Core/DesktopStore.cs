using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MacDesk.Core {
 // desktop.json persistence: validate, atomic replace, hashed backups, cross-process lock.
 public class DesktopStore {
  public string Root { get; private set; }
  public DesktopData Data { get; private set; }
  string original;
  readonly object gate = new object();
  static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }; }
  static string Hash(byte[] b) { using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(b)); }
  public DesktopStore(string root) { Root = NormalizeRoot(root); Reload(); }
  static string NormalizeRoot(string root) { string full = Path.GetFullPath(root).Replace('/', '\\'); string volume = Path.GetPathRoot(full); return full.Length == volume.Length ? full : full.TrimEnd('\\'); }
  // Cooperating DesktopStore writers share this mutex. Hash checking is NOT an
  // atomic CAS against arbitrary external editors which ignore this protocol.
  IDisposable AcquireWriteLock() { string identity = Root.TrimEnd('\\').ToUpperInvariant(); string name = "Local\\MacDeskNative.Store." + Hash(Encoding.UTF8.GetBytes(identity)).Replace('/', '_').Replace('+', '-').TrimEnd('='); return new WriterLock(name); }
  sealed class WriterLock : IDisposable {
   Mutex mutex; bool held;
   public WriterLock(string name) {
    mutex = new Mutex(false, name);
    try {
     try { held = mutex.WaitOne(30000); } catch (AbandonedMutexException) { held = true; }
     if (!held) throw new IOException("其他MacDesk进程正在保存，请稍后重试。");
    } catch { mutex.Dispose(); throw; }
   }
   public void Dispose() { try { if (held) mutex.ReleaseMutex(); } finally { mutex.Dispose(); } }
  }
  public static string NewId() { return "G" + Guid.NewGuid().ToString("N"); }
  public void Reload() {
   lock (gate) {
    string path = Path.Combine(Root, "desktop.json");
    byte[] b = File.Exists(path) ? File.ReadAllBytes(path) : null;
    DesktopData next = b == null ? new DesktopData() : Serializer().Deserialize<DesktopData>(Encoding.UTF8.GetString(b).TrimStart('\uFEFF'));
    Validate(next);
    Data = next;
    original = b == null ? null : Hash(b);
   }
  }
  public static void Validate(DesktopData d) {
   if (d == null || d.version != 1 || d.groups == null) throw new InvalidDataException("不支持的桌面数据版本或格式。");
   if (d.notes == null) d.notes = new Dictionary<string, string>();
   foreach (var pair in d.notes) {
    if (String.IsNullOrWhiteSpace(pair.Key) || !Regex.IsMatch(pair.Key, @"\A\d{4}-\d{2}-\d{2}\z"))
     throw new InvalidDataException("日历备忘日期格式无效。");
    if (pair.Value != null && pair.Value.IndexOfAny(new char[] { '\0', '\r', '\n' }) >= 0)
     throw new InvalidDataException("日历备忘不能包含控制字符。");
   }
   var ids = new HashSet<string>(StringComparer.Ordinal);
   foreach (var g in d.groups) {
    if (g == null) throw new InvalidDataException("分类为空。");
    CheckId(g.id, ids);
    if (g.desktopX < -1 || g.desktopY < -1 || g.desktopX > 32767 || g.desktopY > 32767 || ((g.desktopX == -1) != (g.desktopY == -1)))
     throw new InvalidDataException("文件夹位置无效。");
    if (String.IsNullOrWhiteSpace(g.title) || g.items == null) throw new InvalidDataException("分类名称或条目列表无效。");
    foreach (var i in g.items) {
     if (i == null) throw new InvalidDataException("条目为空。");
     CheckId(i.id, ids);
     if (String.IsNullOrWhiteSpace(i.title)) throw new InvalidDataException("条目名称不能为空。");
     EntryServices.ValidateTarget(i.type, i.target, false);
     if (!String.IsNullOrEmpty(i.icon) && !Path.IsPathRooted(i.icon)) throw new InvalidDataException("图标必须是完整路径。");
     if (i.arguments != null && i.arguments.IndexOf('\0') >= 0) throw new InvalidDataException("启动参数包含无效字符。");
    }
   }
  }
  static void CheckId(string id, HashSet<string> ids) { if (id == null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z") || !ids.Add(id)) throw new InvalidDataException("ID无效或重复。"); }
  public void Edit(Action<DesktopData> mutation) {
   if (mutation == null) throw new ArgumentNullException("mutation");
   lock (gate) {
    using (var writeLock = AcquireWriteLock()) {
     var s = Serializer();
     var next = s.Deserialize<DesktopData>(s.Serialize(Data));
     mutation(next);
     Validate(next);
     byte[] bytes = new UTF8Encoding(false).GetBytes(s.Serialize(next));
     Directory.CreateDirectory(Root);
     string path = Path.Combine(Root, "desktop.json"), temp = Path.Combine(Root, ".desktop-" + Guid.NewGuid().ToString("N") + ".tmp");
     try {
      using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); }
      if (File.Exists(path)) {
       using (var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
        byte[] old; using (var ms = new MemoryStream()) { current.CopyTo(ms); old = ms.ToArray(); }
        if (original == null || Hash(old) != original) throw new IOException("配置已被其他程序更新，请重新加载后重试。");
        string backups = Path.Combine(Root, "Backups"); Directory.CreateDirectory(backups);
        string backup = Path.Combine(backups, "data-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(backup, old);
        File.Replace(temp, path, null);
       }
      } else {
       if (original != null) throw new IOException("原配置已被删除，请重新加载。");
       File.Move(temp, path);
      }
      Data = next;
      original = Hash(bytes);
     } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
   }
  }
 }
}
