using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace libCommon
{
    //Copied from: C:\Users\fiddy\Desktop\dev\cs\referencesource-master\System\services\monitoring\system\diagnosticts\ProcessManager.cs
    public class MainWindowFinder
    {
        IntPtr bestHandle;
        int processId;

        public IntPtr FindMainWindow(int processId)
        {
            bestHandle = IntPtr.Zero;
            this.processId = processId;

            EnumThreadWindowsCallback callback = new(EnumWindowsCallback);
            EnumWindows(callback, IntPtr.Zero);

            GC.KeepAlive(callback);
            return bestHandle;
        }

        public IntPtr FindMainWindow(int processId, IntPtr desktopHandle)
        {
            bestHandle = IntPtr.Zero;
            this.processId = processId;

            EnumDesktopWindows(desktopHandle, (handle, lParam) =>
            {
                _ = GetWindowThreadProcessId(new HandleRef(this, handle), out int processId);
                if (processId == this.processId)
                {
                    if (IsMainWindow(handle))
                    {
                        bestHandle = handle;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return bestHandle;
        }

        bool IsMainWindow(IntPtr handle)
        {

            if (GetWindow(new HandleRef(this, handle), GW_OWNER) != IntPtr.Zero || !IsWindowVisible(new HandleRef(this, handle)))
                return false;

            // Microsoft: should we use no window title to mean not a main window? (task man does)

            /*
            int length = NativeMethods.GetWindowTextLength(handle) * 2;
            StringBuilder builder = new StringBuilder(length);
            if (NativeMethods.GetWindowText(handle, builder, builder.Capacity) == 0)
                return false;
            if (builder.ToString() == string.Empty)
                return false;
            */

            return true;
        }

        bool EnumWindowsCallback(IntPtr handle, IntPtr extraParameter)
        {
            _ = GetWindowThreadProcessId(new HandleRef(this, handle), out int processId);
            if (processId == this.processId)
            {
                if (IsMainWindow(handle))
                {
                    bestHandle = handle;
                    return false;
                }
            }
            return true;
        }

        //Copied from: C:\Users\fiddy\Desktop\dev\cs\referencesource-master\System\compmod\microsoft\win32\NativeMethods.cs
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetWindowThreadProcessId(HandleRef handle, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool EnumWindows(EnumThreadWindowsCallback callback, IntPtr extraData);

        delegate bool EnumThreadWindowsCallback(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto)]
        static extern IntPtr GetWindow(HandleRef hWnd, int uCmd);

        public const int GW_OWNER = 4;

        [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern bool IsWindowVisible(HandleRef hWnd);

        [DllImport("user32.dll", EntryPoint = "EnumDesktopWindows", ExactSpelling = false, CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumDelegate lpEnumCallbackFunction, IntPtr lParam);

        public delegate bool EnumDelegate(IntPtr hWnd, int lParam);
    }
}
