using System;
using System.Runtime.InteropServices;
using System.Text;

using MacDesk.Core;

namespace MacDeskNative
{
    /// <summary>
    /// Selects an EXISTING visible Shell desktop view for creation-time WS_CHILD hosting.
    /// Hosts MacDesk's windows and manages the native desktop icon view during a session.
    /// </summary>
    public static class NativeDesktopHost
    {
        // Current Home's expected solid screen point; not a general multi-monitor policy.
        private const int HomePointX = 240;
        private const int HomePointY = 368;
        private const uint GA_ROOT = 2;

        public static IntPtr Parent { get; private set; }
        public static string Error { get; private set; }

        public static void DisableChildHosting(string reason)
        {
            Parent = IntPtr.Zero;
            Error = reason;
        }

        public static void ResetStaleHandle(System.Windows.Forms.Control control)
        {
            try
            {
                var wf = typeof(System.Windows.Forms.Control).GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (wf != null)
                {
                    var nw = wf.GetValue(control) as System.Windows.Forms.NativeWindow;
                    if (nw != null && nw.Handle != IntPtr.Zero)
                    {
                        DestroyWindow(nw.Handle);
                        nw.ReleaseHandle();
                    }
                }
            }
            catch { }
        }

#if DESKTOP_HOST_TESTS
        // Hidden fake-host tests only: do not discover Shell, change DPI, or write any HWND.
        internal static void SetTestParent(IntPtr parent)
        {
            Parent = parent;
            Error = null;
        }
#endif

        private static bool prepareCalled;
        private static uint uiThread;
        private static uint shellPid;
        private static IntPtr shellWindow;
        private static IntPtr shellRoot;
        private static IntPtr hiddenIconList, iconView;
        private const int WindowStyle = -16, VisibleStyle = 0x10000000;
        private const int HideWindow = 0, ShowNoActivate = 8;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr window);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint GetCurrentThreadId();

        internal static void HideDesktopIcons(IntPtr widgets, IntPtr folders)
        {
            if (hiddenIconList != IntPtr.Zero || !IsHosted(widgets) || !IsHosted(folders))
                return;
            IntPtr view = Parent;
            IntPtr list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (!IsIconList(view, list) || (GetWindowStyle(list, WindowStyle) & VisibleStyle) == 0)
                return;
            if (ShowWindowAsync(list, HideWindow))
            {
                iconView = view;
                hiddenIconList = list;
            }
        }

        internal static void RestoreDesktopIcons()
        {
            IntPtr list = hiddenIconList;
            hiddenIconList = IntPtr.Zero;
            if (IsIconList(iconView, list))
                ShowWindowAsync(list, ShowNoActivate);
            iconView = IntPtr.Zero;
        }

        private static bool IsIconList(IntPtr view, IntPtr list)
        {
            return view != IntPtr.Zero && list != IntPtr.Zero && IsSameShell(shellWindow, shellPid) &&
                HasPid(view, shellPid) && HasPid(list, shellPid) && GetParent(list) == view &&
                ClassName(list) == "SysListView32" && ClassName(view) == "SHELLDLL_DefView";
        }

        public static void EnsureChildTopOrder(IntPtr primaryChild, IntPtr secondaryChild)
        {
            IntPtr parent = Parent;
            if (parent == IntPtr.Zero || !IsWindow(parent))
                return;
            bool hasPrimary = primaryChild != IntPtr.Zero && IsWindow(primaryChild) && GetParent(primaryChild) == parent;
            bool hasSecondary = secondaryChild != IntPtr.Zero && IsWindow(secondaryChild) && GetParent(secondaryChild) == parent;
            if (!hasPrimary && !hasSecondary)
                return;

            IntPtr first = GetWindow(parent, 5); // GW_CHILD
            bool alreadyOrdered = hasPrimary
                ? (first == primaryChild && (!hasSecondary || GetWindow(primaryChild, 2) == secondaryChild))
                : (first == secondaryChild);
            if (alreadyOrdered)
                return;

            const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
            if (hasSecondary)
                SetWindowPos(secondaryChild, IntPtr.Zero, 0, 0, 0, 0, flags);
            if (hasPrimary)
                SetWindowPos(primaryChild, IntPtr.Zero, 0, 0, 0, 0, flags);
        }

        public static object[] DescribeHostChildren()
        {
            IntPtr parent = Parent;
            if (parent == IntPtr.Zero || !IsWindow(parent))
                return new object[0];
            var list = new System.Collections.Generic.List<object>();
            IntPtr cur = GetWindow(parent, 5); // GW_CHILD
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 64)
            {
                uint pid;
                GetWindowThreadProcessId(cur, out pid);
                list.Add(new
                {
                    handle = cur.ToInt64(),
                    className = ClassName(cur),
                    pid = pid,
                    visible = IsWindowVisible(cur)
                });
                cur = GetWindow(cur, 2); // GW_HWNDNEXT
            }
            return list.ToArray();
        }

        /// <summary>
        /// Call once on the UI thread BEFORE constructing the real App or any of its HWNDs;
        /// do not call from self-test. Retain the result until ALL App windows are destroyed,
        /// then Dispose on this same thread. The context applies to all intervening HWNDs.
        /// The caller creates Home with CP.Parent = Parent, WS_CHILD, and no WS_POPUP.
        /// Parent == zero means explicit failed hosting / old popup fallback, not success.
        /// Repeated calls are inert and do not acquire another DPI-context lifetime.
        /// </summary>
        public static IDisposable Prepare()
        {
            if (prepareCalled)
                return new EmptyScope();

            prepareCalled = true;
            Parent = IntPtr.Zero;
            Error = null;
            DpiScope scope = null;
            try
            {
                uiThread = GetCurrentThreadId();
                IntPtr shell = GetShellWindow();
                uint pid;
                if (shell == IntPtr.Zero || !IsWindow(shell) ||
                    GetWindowThreadProcessId(shell, out pid) == 0 || pid == 0)
                    return Fail("GetShellWindow did not identify a live Shell process.", null);

                IntPtr root;
                IntPtr view;
                string discoveryError;
                if (!FindDesktopView(shell, pid, out root, out view, out discoveryError))
                    return Fail(discoveryError, null);

                IntPtr context = GetWindowDpiAwarenessContext(view);
                if (context == IntPtr.Zero)
                    return Fail(NativeError("GetWindowDpiAwarenessContext"), null);

                IntPtr previous = SetThreadDpiAwarenessContext(context);
                if (previous == IntPtr.Zero)
                    return Fail(NativeError("SetThreadDpiAwarenessContext"), null);
                scope = new DpiScope(previous, uiThread);

                // Never publish a parent until the creation thread really matches its DPI.
                IntPtr current = GetThreadDpiAwarenessContext();
                if (current == IntPtr.Zero || !AreDpiAwarenessContextsEqual(current, context))
                    return Fail("UI thread did not enter the Shell parent's DPI context.", scope);

                // Discovery and DPI switching race Explorer shutdown/restart. Recheck before
                // publishing; this still cannot make future HWND creation atomic with Shell.
                if (!IsSameShell(shell, pid) || !IsDesktopView(root, view, pid))
                    return Fail("Shell desktop view changed while preparing child hosting.", scope);
                IntPtr checkedContext = GetWindowDpiAwarenessContext(view);
                if (checkedContext == IntPtr.Zero ||
                    !AreDpiAwarenessContextsEqual(current, checkedContext))
                    return Fail("Shell desktop view DPI context changed during preparation.", scope);

                shellWindow = shell;
                shellPid = pid;
                shellRoot = root;
                Parent = view;
                return scope;
            }
            catch (EntryPointNotFoundException ex)
            {
                return Fail("Required Windows DPI/desktop API is unavailable: " + ex.Message, scope);
            }
            catch (DllNotFoundException ex)
            {
                return Fail("Required Windows desktop library is unavailable: " + ex.Message, scope);
            }
            catch (Exception ex)
            {
                return Fail("Desktop host preparation failed: " + ex.GetType().Name + ": " + ex.Message, scope);
            }
        }

        /// <summary>
        /// Read-only observation: no HWND is created and no static state is repaired here.
        /// False includes stale/lost Shell parents. Caller must fail safe, never reparent a
        /// live Home. Explorer restart/host loss recovery is future lifecycle work; this
        /// foundation alone does not establish Win+D or restart behavior as tested.
        /// </summary>
        public static bool IsHosted(IntPtr child)
        {
            try
            {
                IntPtr parent = Parent;
                return parent != IntPtr.Zero && child != IntPtr.Zero &&
                    GetCurrentThreadId() == uiThread && IsWindow(child) &&
                    GetParent(child) == parent && IsSameShell(shellWindow, shellPid) &&
                    IsDesktopView(GetParent(parent), parent, shellPid); // Shell legitimately moves this same view between its verified Progman/WorkerW roots during Win+D
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool FindDesktopView(IntPtr shell, uint pid,
            out IntPtr root, out IntPtr view, out string error)
        {
            root = IntPtr.Zero;
            view = FindDirectView(shell, pid);
            error = null;
            if (view != IntPtr.Zero)
            {
                root = shell;
                return true;
            }

            IntPtr selectedRoot = IntPtr.Zero;
            IntPtr selectedView = IntPtr.Zero;
            string callbackError = null;
            // EnumWindows supplies only top-level windows. FindWindowEx below only searches
            // direct children: never walk arbitrary process trees or target another process.
            bool completed = EnumWindows(delegate(IntPtr candidate, IntPtr parameter)
            {
                try
                {
                    if (candidate == shell)
                        return true;
                    IntPtr found = FindDirectView(candidate, pid);
                    if (found == IntPtr.Zero)
                        return true;
                    selectedRoot = candidate;
                    selectedView = found;
                    return false;
                }
                catch (Exception ex)
                {
                    // Do not unwind a managed exception through the unmanaged callback.
                    callbackError = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }, IntPtr.Zero);
            if (selectedView != IntPtr.Zero)
            {
                root = selectedRoot;
                view = selectedView;
                return true;
            }
            error = callbackError != null ? "Shell enumeration failed: " + callbackError :
                !completed ? NativeError("EnumWindows") :
                "No existing visible same-Shell Progman/WorkerW direct SHELLDLL_DefView covers Home point (240,368).";
            return false;
        }

        private static IntPtr FindDirectView(IntPtr root, uint pid)
        {
            if (!IsDesktopRoot(root, pid))
                return IntPtr.Zero;
            IntPtr after = IntPtr.Zero;
            while (true)
            {
                IntPtr view = FindWindowEx(root, after, "SHELLDLL_DefView", null);
                if (view == IntPtr.Zero)
                    return IntPtr.Zero;
                if (IsDesktopView(root, view, pid))
                    return view;
                if (view == after)
                    return IntPtr.Zero;
                after = view;
            }
        }

        private static bool IsSameShell(IntPtr shell, uint pid)
        {
            return pid != 0 && shell != IntPtr.Zero && GetShellWindow() == shell &&
                IsWindow(shell) && HasPid(shell, pid);
        }

        private static bool IsDesktopRoot(IntPtr root, uint pid)
        {
            if (pid == 0 || root == IntPtr.Zero || !IsWindow(root) || !HasPid(root, pid) ||
                GetAncestor(root, GA_ROOT) != root || !IsWindowVisible(root) || IsIconic(root))
                return false;
            string name = ClassName(root);
            return (name == "Progman" || name == "WorkerW") && CoversHomePoint(root);
        }

        private static bool IsDesktopView(IntPtr root, IntPtr view, uint pid)
        {
            return IsDesktopRoot(root, pid) && view != IntPtr.Zero && IsWindow(view) &&
                HasPid(view, pid) && ClassName(view) == "SHELLDLL_DefView" &&
                GetParent(view) == root && IsWindowVisible(view) && !IsIconic(view) &&
                CoversHomePoint(view);
            // SysListView32 may be hidden: its visibility is deliberately not a criterion.
        }

        private static bool HasPid(IntPtr window, uint expected)
        {
            uint actual;
            return GetWindowThreadProcessId(window, out actual) != 0 && actual == expected;
        }

        private static string ClassName(IntPtr window)
        {
            StringBuilder name = new StringBuilder(128);
            return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : String.Empty;
        }

        private static bool CoversHomePoint(IntPtr window)
        {
            RECT rect;
            return GetWindowRect(window, out rect) && rect.Right > rect.Left &&
                rect.Bottom > rect.Top && rect.Left <= HomePointX && HomePointX < rect.Right &&
                rect.Top <= HomePointY && HomePointY < rect.Bottom;
        }

        private static string NativeError(string operation)
        {
            return operation + " failed (Win32 " + Marshal.GetLastWin32Error() + ").";
        }

        private static IDisposable Fail(string error, DpiScope scope)
        {
            Parent = IntPtr.Zero;
            Error = error;
            if (scope != null)
            {
                // Restore immediately on failed preparation so fallback cannot accidentally
                // create popup windows in a partially prepared Shell DPI context.
                scope.Dispose();
                if (!scope.Restored)
                    return scope; // Retain the saved context for a same-thread retry at exit.
            }
            return new EmptyScope();
        }

        private sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class DpiScope : IDisposable
        {
            private readonly IntPtr previous;
            private readonly uint thread;
            public bool Restored { get; private set; }

            public DpiScope(IntPtr previousContext, uint owningThread)
            {
                previous = previousContext;
                thread = owningThread;
            }

            public void Dispose()
            {
                if (Restored)
                    return;
                if (GetCurrentThreadId() != thread)
                    throw new InvalidOperationException("Desktop host scope must be disposed on its creating UI thread, after all App windows are destroyed.");

                Parent = IntPtr.Zero;
                shellWindow = shellRoot = IntPtr.Zero;
                shellPid = 0;
                try
                {
                    if (SetThreadDpiAwarenessContext(previous) != IntPtr.Zero)
                    {
                        Restored = true;
                        return;
                    }
                    AddRestoreError(NativeError("Restore SetThreadDpiAwarenessContext"));
                }
                catch (Exception ex)
                {
                    AddRestoreError("DPI context restore failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                // A failed restore remains retryable; no finalizer (wrong-thread restore).
            }

            private static void AddRestoreError(string error)
            {
                Error = String.IsNullOrEmpty(Error) ? error : Error + "; " + error;
            }
        }
    }
}
