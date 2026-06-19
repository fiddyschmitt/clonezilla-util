using libCommon;
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

namespace libUIHelpers
{
    public static class WindowHandleHelper
    {
        const uint WM_GETTEXT = 0x0D;
        const uint WM_SETTEXT = 0x000C;

        public static IntPtr? GetRootWindowByTitle(int pid, IntPtr? desktopHandle, Func<string, bool> condition)
        {
            var rootWindows = GetRootWindowsOfProcess(pid, desktopHandle);

            var titles = rootWindows
                            .Select(hWnd => new
                            {
                                Title = GetWindowText(hWnd),
                                Handle = hWnd
                            })
                            .ToList();

            var result = titles
                            .FirstOrDefault(child => condition(child.Title));

            return result?.Handle;
        }

        public static IntPtr? GetChildHandleByText(int pid, IntPtr? desktopHandle, Func<string, bool> condition)
        {
            var result = GetChildHandle(pid, desktopHandle, childHandle =>
            {
                var text = GetWindowText(childHandle);
                var matchesCondition = condition(text);
                return matchesCondition;
            });

            return result;
        }

        public static IntPtr? GetChildHandleByText(IntPtr parentHandle, Func<string, bool> condition)
        {
            var result = GetChildHandle(parentHandle, childHandle =>
            {
                var text = GetWindowText(childHandle);
                var matchesCondition = condition(text);
                return matchesCondition;
            });

            return result;
        }

        public static IntPtr? GetChildHandle(int pid, IntPtr? desktopHandle, Func<IntPtr, bool> childFilter)
        {
            var rootWindows = GetRootWindowsOfProcess(pid, desktopHandle);

            var childWindows = rootWindows
                                .SelectMany(rootWindow => GetChildWindows(rootWindow))
                                .ToList();

            var result = childWindows
                            .FirstOrDefault(child => childFilter(child));

            return result;
        }

        public static IntPtr? GetChildHandle(IntPtr parentHandle, Func<IntPtr, bool> childFilter)
        {
            var childWindows = GetChildWindows(parentHandle);

            var result = childWindows
                            .FirstOrDefault(child => childFilter(child));

            return result;
        }

        public static List<IntPtr> GetRootWindowsOfProcess(int pid, IntPtr? desktopHandle)
        {
            List<IntPtr> rootWindows;
            if (desktopHandle == null)
            {
                rootWindows = GetChildWindows(IntPtr.Zero); //Couldn't we just give the pid to this call, to save us iterating over all windows?
            }
            else
            {
                rootWindows = [];

                WNDENUMPROC callback = (HWND handle, LPARAM lParam) =>
                {
                    rootWindows.Add(handle);
                    return true;
                };
                PInvoke.EnumDesktopWindows((HDESK)desktopHandle.Value, callback, (LPARAM)0);
                GC.KeepAlive(callback);
            }

            var dsProcRootWindows = new List<IntPtr>();
            foreach (IntPtr hWnd in rootWindows)
            {
                _ = PInvoke.GetWindowThreadProcessId((HWND)hWnd, out uint lpdwProcessId);
                if (lpdwProcessId == pid)
                {
                    dsProcRootWindows.Add(hWnd);
                }
            }
            return dsProcRootWindows;
        }

        public static List<(IntPtr Handle, string ControlClass, string Text, string ClassNN)> GetChildWindowsDetailsRecursively(IntPtr handle)
        {
            var result = GetChildWindowsDetailsRecursively([handle]);
            return result;
        }

        public static List<(IntPtr Handle, string ControlClass, string Text, string ClassNN)> GetChildWindowsDetailsRecursively(List<IntPtr> handles)
        {
            var allHandles = handles
                                .SelectMany(h => GetChildWindowsRecursively(h))
                                .ToList();

            var classCounts = new Dictionary<string, int>();

            var result = allHandles
                            .Select(h =>
                            {
                                var text = GetWindowText(h);
                                var className = GetClassName(h);

                                if (!classCounts.TryGetValue(className, out int value))
                                {
                                    value = 0;
                                    classCounts.Add(className, value);
                                }

                                classCounts[className] = value + 1;

                                var classNN = $"{className}{classCounts[className]}";

                                return (h, className, text, classNN);
                            })
                            .ToList();

            return result;
        }

        public static string GetClassName(IntPtr handle)
        {
            Span<char> buffer = stackalloc char[256];
            int len = PInvoke.GetClassName((HWND)handle, buffer);

            var result = new string(buffer[..len]);
            return result;
        }

        //SendMessage(textBox1.Handle, WM_SETTEXT, IntPtr.Zero,
        public static unsafe void SetWindowText(IntPtr handle, string text)
        {
            fixed (char* p = text)
            {
                _ = PInvoke.SendMessage((HWND)handle, WM_SETTEXT, default, (LPARAM)(nint)p);
            }
        }

        public static unsafe string GetWindowText(IntPtr handle)
        {
            Span<char> buffer = stackalloc char[255];
            int len;
            fixed (char* p = buffer)
            {
                len = (int)(nint)PInvoke.SendMessage((HWND)handle, WM_GETTEXT, (WPARAM)(nuint)buffer.Length, (LPARAM)(nint)p);
            }

            var result = new string(buffer[..len]);
            return result;
        }

        public static List<IntPtr> GetChildWindowsRecursively(IntPtr hWnd)
        {
            var result = Extensions.Recurse([hWnd], hParent =>
            {
                var childWindows = GetChildWindows(hParent);

                return childWindows;
            }).ToList();

            return result;
        }

        public static List<IntPtr> GetChildWindows(IntPtr parentHandle)
        {
            var result = new List<IntPtr>();
            GCHandle listHandle = GCHandle.Alloc(result);
            try
            {
                WNDENUMPROC childProc = EnumWindow;
                PInvoke.EnumChildWindows((HWND)parentHandle, childProc, (LPARAM)GCHandle.ToIntPtr(listHandle));
                GC.KeepAlive(childProc);
            }
            finally
            {
                if (listHandle.IsAllocated)
                    listHandle.Free();
            }
            return result;
        }

        private static BOOL EnumWindow(HWND handle, LPARAM pointer)
        {
            GCHandle gch = GCHandle.FromIntPtr((nint)pointer);
            if (gch.Target is not List<IntPtr> list)
            {
                throw new InvalidCastException("GCHandle Target could not be cast as List<IntPtr>");
            }
            list.Add(handle);
            //  You can modify this to check to see if you want to cancel the operation, then return a null here
            return true;
        }
    }
}
