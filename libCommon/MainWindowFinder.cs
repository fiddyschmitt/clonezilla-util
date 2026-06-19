using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.WindowsAndMessaging;

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

            WNDENUMPROC callback = EnumWindowsCallback;
            PInvoke.EnumWindows(callback, (LPARAM)0);

            GC.KeepAlive(callback);
            return bestHandle;
        }

        public IntPtr FindMainWindow(int processId, IntPtr desktopHandle)
        {
            bestHandle = IntPtr.Zero;
            this.processId = processId;

            WNDENUMPROC callback = EnumWindowsCallback;
            PInvoke.EnumDesktopWindows((HDESK)desktopHandle, callback, (LPARAM)0);

            GC.KeepAlive(callback);
            return bestHandle;
        }

        bool IsMainWindow(HWND handle)
        {

            if (PInvoke.GetWindow(handle, (GET_WINDOW_CMD)GW_OWNER) != default || !PInvoke.IsWindowVisible(handle))
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

        BOOL EnumWindowsCallback(HWND handle, LPARAM extraParameter)
        {
            _ = PInvoke.GetWindowThreadProcessId(handle, out uint pid);
            if (pid == this.processId)
            {
                if (IsMainWindow(handle))
                {
                    bestHandle = handle;
                    return false;
                }
            }
            return true;
        }

        public const int GW_OWNER = 4;
    }
}
