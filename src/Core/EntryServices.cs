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
 // Entry targets: validation, creating entries from paths/URLs, launching via the Shell,
 // and discovering installed apps from the Start menu / desktop.
 public static class EntryServices {
  static readonly object gate = new object();
  public static string ValidateTarget(string type, string target, bool exists) {
   if (String.IsNullOrWhiteSpace(target) || target.IndexOfAny(new char[] { '\0', '\r', '\n' }) >= 0) throw new InvalidDataException("目标不能为空或包含控制字符。");
   if (type == "url") {
    Uri u; if (!Uri.TryCreate(target, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) throw new InvalidDataException("只支持完整http/https网址。");
    return u.AbsoluteUri;
   }
   if (type != "app" && type != "file" && type != "folder") throw new InvalidDataException("不支持的入口类型。");
   if (!IsAbsolutePath(target)) throw new InvalidDataException("目标必须是绝对路径，例如 C:\\Apps\\App.exe 或 \\\\server\\share\\file；不支持驱动器相对或根相对路径。");
   string full = Path.GetFullPath(target);
   if (exists && (type == "folder" ? !Directory.Exists(full) : !File.Exists(full))) throw new FileNotFoundException("目标已不存在，请编辑入口更新路径。", full);
   return full;
  }
  public static bool IsAbsolutePath(string path) {
   if (String.IsNullOrWhiteSpace(path)) return false;
   string p = path.Replace('/', '\\');
   if (Regex.IsMatch(p, @"\A[A-Za-z]:\\")) return true;
   if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
   string[] parts = p.Substring(2).Split('\\');
   return parts.Length >= 2 && !String.IsNullOrWhiteSpace(parts[0]) && !String.IsNullOrWhiteSpace(parts[1]) && parts[0] != "?" && parts[0] != "." && parts[1] != "." && parts[1] != "..";
  }
  public static DeskItem FromPath(string path) {
   string full = ValidateTarget("file", path, false);
   bool folder = Directory.Exists(full);
   if (!folder && !File.Exists(full)) throw new FileNotFoundException("文件或文件夹不存在。", full);
   string ext = Path.GetExtension(full);
   string title = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
   if (String.IsNullOrEmpty(title)) title = full;
   bool shortcut = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
   bool executable = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
   if (!folder && (shortcut || executable))
    title = Path.GetFileNameWithoutExtension(title);
   string type = folder ? "folder" : (executable || shortcut ? "app" : "file");
   var item = new DeskItem { id = DesktopStore.NewId(), title = title, description = "", type = type, target = full, icon = "", arguments = "" };
   NormalizeAppShortcut(item);
   return item;
  }
  // Keep the .lnk as the launch target so Windows still applies its icon, arguments and working directory.
  internal static void NormalizeAppShortcut(DeskItem item) {
   if (item.type == "app" && item.target != null && item.target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
       !ShortcutLaunchesApp(item.target))
    item.type = "file";
  }
  static bool ShortcutLaunchesApp(string path) {
   if (!File.Exists(path)) return false;
   object shell = null, shortcut = null;
   try {
    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
    if (shellType == null) return false;
    shell = Activator.CreateInstance(shellType);
    shortcut = ((dynamic)shell).CreateShortcut(path);
    string target = ((dynamic)shortcut).TargetPath;
    const string appsFolder = "shell:AppsFolder\\";
    if (String.IsNullOrEmpty(target)) return false;
    if (target.StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase)) return true;
    if (String.Equals(Path.GetFileName(target), "explorer.exe", StringComparison.OrdinalIgnoreCase)) {
     string args = ((dynamic)shortcut).Arguments;
     return String.IsNullOrWhiteSpace(args) || args.TrimStart().StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase);
    }
    switch (Path.GetExtension(target).ToLowerInvariant()) {
     case ".exe": case ".com": case ".bat": case ".cmd": case ".msc": case ".cpl": case ".appref-ms":
      return !Directory.Exists(target);
     default:
      return false;
    }
   } catch (COMException) {
    return false;
   } catch (ArgumentException) {
    return false;
   } finally {
    Release(shortcut);
    Release(shell);
   }
  }
  public static DeskItem FromUrl(string url) {
   string full = ValidateTarget("url", url, false);
   var u = new Uri(full);
   return new DeskItem { id = DesktopStore.NewId(), title = u.Host, description = "", type = "url", target = full, icon = "", arguments = "" };
  }
  public static void Open(DeskItem item) {
   if (item == null) throw new ArgumentNullException("item");
   string target = ValidateTarget(item.type, item.target, true);
   ShellExec(target, item.type == "app" ? (item.arguments ?? "") : "", item.type == "url" ? "" : Path.GetDirectoryName(target));
  }
  static void ShellExec(string target, string arguments, string workingDirectory) {
   object shell = null, windows = null, desktop = null, document = null, application = null;
   try {
    shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
    dynamic sh = shell;
    windows = sh.Windows();
    dynamic ws = windows;
    int hwnd = 0;
    desktop = ws.FindWindowSW(0, 0, 8, out hwnd, 1);
    if (desktop == null) throw new InvalidOperationException("Explorer桌面暂时不可用。");
    dynamic desk = desktop;
    document = desk.Document;
    dynamic doc = document;
    application = doc.Application;
    dynamic app = application;
    app.ShellExecute(target, arguments ?? "", workingDirectory ?? "", "open", 1);
   } finally {
    Release(application); Release(document); Release(desktop); Release(windows); Release(shell);
   }
  }
  public static string ResolveDirectory(DeskItem item) {
   if (item == null || item.type == "url") return null;
   string full = ValidateTarget(item.type, item.target, true);
   return item.type == "folder" ? full : Path.GetDirectoryName(full);
  }
  public static void OpenContainingFolder(DeskItem item) {
   if (item == null || item.type == "url") return;
   string full = ValidateTarget(item.type, item.target, true);
   if (item.type == "folder") {
    ShellExec(full, "", full);
   } else {
    string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    ShellExec(explorer, "/select,\"" + full + "\"", Path.GetDirectoryName(full));
   }
  }
  public static void OpenInTerminal(DeskItem item) {
   string dir = ResolveDirectory(item);
   if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new DirectoryNotFoundException("找不到目标目录。");
   string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
   if (File.Exists(wt)) {
    ShellExec(wt, "-d \"" + dir + "\"", dir);
   } else {
    string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
    ShellExec(ps, "-NoExit -Command \"Set-Location -LiteralPath '" + dir.Replace("'", "''") + "'\"", dir);
   }
  }
  public static string FindEditorExecutable(string kind) {
   string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
   string prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
   string[] candidates = kind == "cursor"
    ? new[] { @"D:\cursor\Cursor.exe", Path.Combine(local, @"Programs\cursor\Cursor.exe"), Path.Combine(prog, @"cursor\Cursor.exe") }
    : new[] { @"D:\Microsoft VS Code\Code.exe", Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"), Path.Combine(prog, @"Microsoft VS Code\Code.exe") };
   foreach (string c in candidates) if (!String.IsNullOrEmpty(c) && File.Exists(c)) return c;
   return null;
  }
  public static void OpenInCodeEditor(DeskItem item, string kind) {
   if (item == null || item.type == "url") return;
   string exe = FindEditorExecutable(kind);
   if (exe == null) throw new FileNotFoundException("未找到对应的代码编辑器安装路径。");
   string full = ValidateTarget(item.type, item.target, true);
   string dir = item.type == "folder" ? full : Path.GetDirectoryName(full);
   ShellExec(exe, "\"" + full + "\"", dir);
  }
  static List<DeskItem> cachedCandidates;
  static DateTime candidatesTime = DateTime.MinValue;
  public static List<DeskItem> DiscoverSystemApps() {
   lock (gate) {
    if (cachedCandidates != null && (DateTime.UtcNow - candidatesTime).TotalSeconds < 45)
     return new List<DeskItem>(cachedCandidates);
   }
   var result = new List<DeskItem>();
   var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   var roots = new List<string>();
   try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
   try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
   if (Directory.Exists(@"D:\00_快捷入口")) roots.Add(@"D:\00_快捷入口");
   try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs)); } catch { }
   try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)); } catch { }

   foreach (string dir in roots) {
    if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
    int maxDepth = dir.IndexOf("Start Menu", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1;
    ScanShortcuts(dir, 0, maxDepth, seen, result);
   }
   lock (gate) {
    cachedCandidates = result;
    candidatesTime = DateTime.UtcNow;
    return new List<DeskItem>(result);
   }
  }
  static void ScanShortcuts(string dir, int depth, int maxDepth, HashSet<string> seen, List<DeskItem> output) {
   if (output.Count >= 60) return;
   try {
    foreach (string file in Directory.GetFiles(dir)) {
     if (output.Count >= 60) return;
     string ext = Path.GetExtension(file);
     if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
     string name = Path.GetFileNameWithoutExtension(file);
     if (String.IsNullOrWhiteSpace(name)) continue;
     if (name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("卸载", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("MacDesk", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("使用说明", StringComparison.OrdinalIgnoreCase) >= 0) continue;
     if (seen.Contains(name)) continue;
     try {
      DeskItem item = FromPath(file);
      if (item.type == "app" && seen.Add(name)) output.Add(item);
     } catch { }
    }
    if (depth < maxDepth) {
     foreach (string sub in Directory.GetDirectories(dir)) {
      ScanShortcuts(sub, depth + 1, maxDepth, seen, output);
     }
    }
   } catch { }
  }
  static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
 }
}
