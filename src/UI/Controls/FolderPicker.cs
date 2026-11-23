using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacDeskNative {
 // The modern Explorer folder picker (IFileOpenDialog with FOS_PICKFOLDERS) instead of the
 // XP-era tree of FolderBrowserDialog. Falls back to FolderBrowserDialog if COM fails.
 internal static class FolderPicker {
  const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
  const uint SIGDN_FILESYSPATH = 0x80058000;
  const int ERROR_CANCELLED = unchecked((int)0x800704C7);

  /// Returns the chosen folder, or null if the user cancelled.
  public static string Pick(IWin32Window owner, string title) {
   IFileDialog dialog = null;
   try {
    dialog = (IFileDialog)new FileOpenDialogCom();
    uint options;
    dialog.GetOptions(out options);
    dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
    if (!String.IsNullOrEmpty(title)) dialog.SetTitle(title);
    int hr = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);
    if (hr == ERROR_CANCELLED) return null;
    if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    IShellItem item;
    dialog.GetResult(out item);
    try {
     string path;
     item.GetDisplayName(SIGDN_FILESYSPATH, out path);
     return path;
    } finally { Marshal.ReleaseComObject(item); }
   } catch (COMException) {
    return Fallback(owner, title);
   } catch (InvalidCastException) {
    return Fallback(owner, title);
   } finally {
    if (dialog != null) Marshal.ReleaseComObject(dialog);
   }
  }

  static string Fallback(IWin32Window owner, string title) {
   using (var f = new FolderBrowserDialog { Description = title ?? "" })
    return f.ShowDialog(owner) == DialogResult.OK ? f.SelectedPath : null;
  }

  [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
  class FileOpenDialogCom { }

  [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IFileDialog {
   [PreserveSig] int Show(IntPtr parent);
   void SetFileTypes(uint count, IntPtr filterSpec);
   void SetFileTypeIndex(uint index);
   void GetFileTypeIndex(out uint index);
   void Advise(IntPtr events, out uint cookie);
   void Unadvise(uint cookie);
   void SetOptions(uint options);
   void GetOptions(out uint options);
   void SetDefaultFolder(IShellItem item);
   void SetFolder(IShellItem item);
   void GetFolder(out IShellItem item);
   void GetCurrentSelection(out IShellItem item);
   void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
   void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
   void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
   void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
   void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
   void GetResult(out IShellItem item);
   void AddPlace(IShellItem item, int placement);
   void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
   void Close(int hr);
   void SetClientGuid(ref Guid guid);
   void ClearClientData();
   void SetFilter(IntPtr filter);
  }

  [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItem {
   void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
   void GetParent(out IShellItem parent);
   void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
   void GetAttributes(uint mask, out uint attributes);
   void Compare(IShellItem other, uint hint, out int order);
  }
 }
}
