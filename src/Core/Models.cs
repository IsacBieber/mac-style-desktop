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
  public int version { get; set; }
  public List<DeskGroup> groups { get; set; }
  public Dictionary<string, string> notes { get; set; }
  public bool showLabels { get; set; }
  public int homeX { get; set; }
  public int homeY { get; set; }
  public DesktopData() {
   version = 1;
   groups = new List<DeskGroup>();
   notes = new Dictionary<string, string>();
   showLabels = false;
   homeX = -1;
   homeY = -1;
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
 }
 public class DeskItem {
  public string id { get; set; }
  public string title { get; set; }
  public string description { get; set; }
  public string type { get; set; }
  public string target { get; set; }
  public string icon { get; set; }
  public string arguments { get; set; }
 }
}
