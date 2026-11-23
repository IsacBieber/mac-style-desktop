using System;
using System.Drawing;

namespace MacDeskNative {
 // The one set of visual tokens (iOS / macOS dark appearance). Every surface - widgets, folder
 // panel, popups, menus and the edit form - takes its colors, corner radii and type sizes from
 // here, so a new feature cannot drift into its own look. Ui keeps the drawing helpers.
 internal static class Theme {
  // ---- System colors ------------------------------------------------------------------
  public static readonly Color Red = Color.FromArgb(255, 69, 58);
  public static readonly Color Orange = Color.FromArgb(255, 159, 10);
  public static readonly Color Yellow = Color.FromArgb(255, 214, 10);
  public static readonly Color Green = Color.FromArgb(48, 209, 88);
  public static readonly Color Blue = Color.FromArgb(10, 132, 255);
  /// Blue for text and glyphs on dark glass (systemBlue is too dark to read there).
  public static readonly Color BlueOnGlass = Color.FromArgb(70, 168, 255);

  // ---- Labels -------------------------------------------------------------------------
  public static readonly Color Label = Color.FromArgb(255, 255, 255);
  public static readonly Color SecondaryLabel = Color.FromArgb(190, 235, 235, 245);
  public static readonly Color TertiaryLabel = Color.FromArgb(120, 235, 235, 245);
  public static readonly Color QuaternaryLabel = Color.FromArgb(72, 235, 235, 245);

  // ---- Fills (translucent, for controls drawn on glass) ----------------------------------
  public static readonly Color Fill = Color.FromArgb(92, 120, 120, 128);
  public static readonly Color SecondaryFill = Color.FromArgb(82, 120, 120, 128);
  public static readonly Color TertiaryFill = Color.FromArgb(61, 120, 120, 128);
  public static readonly Color QuaternaryFill = Color.FromArgb(46, 120, 120, 128);
  /// Light wash behind round buttons and hovered rows on glass, and its hover / pressed step.
  public static readonly Color ControlFill = Color.FromArgb(40, 255, 255, 255);
  public static readonly Color ControlFillHover = Color.FromArgb(66, 255, 255, 255);
  public static readonly Color Separator = Color.FromArgb(150, 84, 84, 88);
  /// Text fields: a dark inset plus a faint light lift (reads the same on any glass).
  public static readonly Color FieldShade = Color.FromArgb(96, 0, 0, 0);
  public static readonly Color FieldLight = Color.FromArgb(24, 255, 255, 255);
  public static readonly Color Selection = Color.FromArgb(150, 10, 132, 255);

  // ---- Menus (solid, like macOS dark menus) ------------------------------------------
  public static readonly Color MenuBackground = Color.FromArgb(38, 38, 42);
  public static readonly Color MenuHighlight = Color.FromArgb(31, 92, 177);
  public static readonly Color MenuDestructiveHighlight = Color.FromArgb(151, 49, 53);
  public static readonly Color MenuText = Color.FromArgb(243, 244, 250);
  public static readonly Color MenuDisabledText = Color.FromArgb(149, 150, 161);
  public static readonly Color MenuDestructiveText = Color.FromArgb(255, 149, 142);
  public static readonly Color MenuSeparator = Color.FromArgb(46, 255, 255, 255);
  /// DWMWA_BORDER_COLOR for our popup windows (COLORREF 0x00BBGGRR).
  public const int WindowBorderColorRef = 0x00504A4A;

  // ---- Solid window (the edit form is a real activating window with native text boxes) --
  public static readonly Color FormBackground = Color.FromArgb(30, 30, 34);
  public static readonly Color FormCell = Color.FromArgb(46, 46, 51);
  public static readonly Color FormCellHover = Color.FromArgb(56, 56, 62);

  // ---- Corner radii (continuous corners, Ui.Squircle) -------------------------------
  public const float WidgetRadius = 26f;
  public const float SheetRadius = Metrics.SheetRadius;
  public const float PopoverRadius = 24f;
  public const float AlertRadius = 22f;
  public const float RowRadius = 10f;

  // ---- Type scale (px) -----------------------------------------------------------------
  public const float LargeTitle = 24f;
  public const float Title = 17f;
  public const float Body = 15f;
  public const float Callout = 14.5f;
  public const float Footnote = 13f;
  public const float Caption = 12f;

  // ---- Motion ----------------------------------------------------------------------------
  // SwiftUI-style (response seconds, damping fraction).
  public const double PopupOpenResponse = 0.32, PopupOpenDamping = 0.86;
  public const double PopupCloseResponse = 0.2, PopupCloseDamping = 1.0;
 }
}
