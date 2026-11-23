using System;
using System.Collections.Generic;

namespace MacDesk.Core {
 // Read-only views over DesktopData.todos for the reminders widget, the day sheet and the
 // calendar dots. Pure functions: "today" is passed in by the caller (the UI uses Beijing time).
 public static class TodoQueries {
  public enum AgendaSection { Overdue, Today, Undated }

  public sealed class AgendaEntry {
   public readonly DeskTodo Todo;
   public readonly AgendaSection Section;
   public AgendaEntry(DeskTodo todo, AgendaSection section) { Todo = todo; Section = section; }
  }

  /// Bit flags per calendar day.
  [Flags]
  public enum DayMark { None = 0, OpenTodos = 1, Note = 2 }

  /// Open reminders that need attention: overdue (oldest day first), due today, then undated.
  /// List order is kept within a day and within each section.
  public static List<AgendaEntry> Agenda(DesktopData d, string todayKey) {
   return Agenda(d, todayKey, t => !t.done);
  }

  internal static List<AgendaEntry> Agenda(DesktopData d, string todayKey, Predicate<DeskTodo> include) {
   var result = new List<AgendaEntry>();
   if (d == null || d.todos == null) return result;
   var overdue = new List<KeyValuePair<int, DeskTodo>>();
   var today = new List<AgendaEntry>();
   var undated = new List<AgendaEntry>();
   for (int i = 0; i < d.todos.Count; i++) {
    var t = d.todos[i];
    AgendaSection section;
    if (!TryAgendaSection(t, todayKey, out section) || !include(t)) continue;
    if (section == AgendaSection.Overdue) overdue.Add(new KeyValuePair<int, DeskTodo>(i, t));
    else (section == AgendaSection.Today ? today : undated).Add(new AgendaEntry(t, section));
   }
   overdue.Sort((a, b) => { int c = String.CompareOrdinal(a.Value.due, b.Value.due); return c != 0 ? c : a.Key - b.Key; });
   foreach (var pair in overdue) result.Add(new AgendaEntry(pair.Value, AgendaSection.Overdue));
   result.AddRange(today);
   result.AddRange(undated);
   return result;
  }

  // The widget only shows a few rows. Count all matches and keep just the leading entries.
  internal static List<AgendaEntry> AgendaPreview(DesktopData d, string todayKey, Predicate<DeskTodo> include, int limit, out int count) {
   var first = new List<AgendaEntry>(limit);
   count = 0;
   if (d == null || d.todos == null) return first;
   foreach (var todo in d.todos) {
    AgendaSection section;
    if (!TryAgendaSection(todo, todayKey, out section) || !include(todo)) continue;
    count++;
    int at = first.Count;
    while (at > 0 && Before(todo, section, first[at - 1])) at--;
    if (at == limit) continue;
    if (first.Count == limit) first.RemoveAt(limit - 1);
    first.Insert(at, new AgendaEntry(todo, section));
   }
   return first;
  }

  static bool Before(DeskTodo todo, AgendaSection section, AgendaEntry other) {
   if (section != other.Section) return section < other.Section;
   return section == AgendaSection.Overdue && String.CompareOrdinal(todo.due, other.Todo.due) < 0;
  }

  static bool TryAgendaSection(DeskTodo todo, string todayKey, out AgendaSection section) {
   if (String.IsNullOrEmpty(todo.due)) { section = AgendaSection.Undated; return true; }
   int dateOrder = String.CompareOrdinal(todo.due, todayKey);
   section = dateOrder < 0 ? AgendaSection.Overdue : AgendaSection.Today;
   return dateOrder <= 0;
  }

  /// Reminders due on dateKey: open ones first, then completed ones, list order within each.
  public static List<DeskTodo> ForDay(DesktopData d, string dateKey) {
   var result = new List<DeskTodo>();
   if (d == null || d.todos == null) return result;
   foreach (var t in d.todos) if (!t.done && t.due == dateKey) result.Add(t);
   foreach (var t in d.todos) if (t.done && t.due == dateKey) result.Add(t);
   return result;
  }

  /// Marks for each day of the month: index = day of month (1..31), index 0 unused.
  public static DayMark[] DayMarkers(DesktopData d, int year, int month) {
   var marks = new DayMark[32];
   if (d == null) return marks;
   string prefix = year.ToString("0000") + "-" + month.ToString("00") + "-";
   if (d.todos != null)
    foreach (var t in d.todos)
     if (!t.done && t.due != null && t.due.StartsWith(prefix, StringComparison.Ordinal)) {
      int day = DayOf(t.due);
      if (day > 0) marks[day] |= DayMark.OpenTodos;
     }
   if (d.notes != null)
    foreach (var pair in d.notes)
     if (!String.IsNullOrEmpty(pair.Value) && pair.Key.StartsWith(prefix, StringComparison.Ordinal)) {
      int day = DayOf(pair.Key);
      if (day > 0) marks[day] |= DayMark.Note;
     }
   return marks;
  }

  static int DayOf(string key) {
   int day;
   return key.Length == 10 && int.TryParse(key.Substring(8, 2), out day) && day >= 1 && day <= 31 ? day : 0;
  }
 }
}
