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
 // Persisted data (desktop.json). Plain DTOs: lower-case names are the JSON field names.
 public class DesktopData {
  /// Version 2 added `todos`. DesktopStore.Reload migrates version 1 files in memory.
  public const int CurrentVersion = 2;
  public int version { get; set; }
  public List<DeskGroup> groups { get; set; }
  public Dictionary<string, string> notes { get; set; }
  public List<DeskTodo> todos { get; set; }
  public bool showLabels { get; set; }
  public int homeX { get; set; }
  public int homeY { get; set; }
  public DesktopData() {
   version = CurrentVersion;
   groups = new List<DeskGroup>();
   notes = new Dictionary<string, string>();
   todos = new List<DeskTodo>();
   showLabels = false;
   homeX = -1;
   homeY = -1;
  }

  internal DesktopData CopyForEdit() {
   var copy = (DesktopData)MemberwiseClone();
   copy.notes = new Dictionary<string, string>(notes, notes.Comparer);
   copy.groups = groups.ConvertAll(group => group.CopyForEdit());
   copy.todos = todos.ConvertAll(todo => todo.CopyForEdit());
   return copy;
  }
 }
 public class DeskGroup {
  public string id { get; set; }
  public string title { get; set; }
  public string subtitle { get; set; }
  public List<DeskItem> items { get; set; }
  public int desktopX { get; set; }
  public int desktopY { get; set; }
  public DeskGroup() {
   items = new List<DeskItem>();
   desktopX = -1;
   desktopY = -1;
  }

  internal DeskGroup CopyForEdit() {
   var copy = (DeskGroup)MemberwiseClone();
   copy.items = items.ConvertAll(item => item.CopyForEdit());
   return copy;
  }
 }
 public class DeskItem {
  public string id { get; set; }
  public string title { get; set; }
  public string description { get; set; }
  public string type { get; set; }
  public string target { get; set; }
  public string icon { get; set; }
  public string arguments { get; set; }

  internal DeskItem CopyForEdit() { return (DeskItem)MemberwiseClone(); }
 }
 /// A reminder. `due` is "" (no date) or "yyyy-MM-dd"; `created` / `completed` are UTC
 /// ISO-8601 stamps ("" while not completed). List order is display order.
 public class DeskTodo {
  public string id { get; set; }
  public string title { get; set; }
  public bool done { get; set; }
  public string due { get; set; }
  public string created { get; set; }
  public string completed { get; set; }
  public DeskTodo() {
   title = "";
   due = "";
   created = "";
   completed = "";
  }

  internal DeskTodo CopyForEdit() { return (DeskTodo)MemberwiseClone(); }
 }
}
