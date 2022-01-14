using libUIHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static libCommon.NativeMethods;

namespace libCommon
{
    public static class DesktopUtility
    {
        static List<IntPtr> DesktopsCreated = new List<IntPtr>();

        public static void Cleanup()
        {
            lock (DesktopsCreated)
            {
                DesktopsCreated
                    .ForEach(deskopHandle =>
                    {
                        try
                        {
                            CloseDesktop(deskopHandle);
                        }
                        catch { }
                    });
            }
        }

        public static (int pid, IntPtr DesktopHandle, IntPtr? WindowHandle) RunProcessOnAnotherDesktop(ProcessStartInfo psi, string desktopName, Func<(int pid, IntPtr DesktopHandle), IntPtr>? waitForWindow)
        {
            //todo: Check if the desktop already exists. Perhaps use EnumDesktop and OpenDesktop
            var hNewDesktop = CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, (uint)DesktopAccess.GenericAll, IntPtr.Zero);

            lock (DesktopsCreated)
            {
                DesktopsCreated.Add(hNewDesktop);
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = desktopName;

            var pi = new PROCESS_INFORMATION();

            var command = $"\"{psi.FileName}\" {psi.Arguments}";

            // start the process.
            CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true, NormalPriorityClass, IntPtr.Zero, null, ref si, ref pi);

            IntPtr? windowToReturn = waitForWindow?.Invoke((pi.dwProcessId, hNewDesktop));
            
            return (pi.dwProcessId, hNewDesktop, windowToReturn);
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref int lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool CloseDesktop(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, int dwFlags,
    uint dwDesiredAccess, IntPtr lpsa);

        [DllImport("kernel32.dll")]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            ref PROCESS_INFORMATION lpProcessInformation
            );

        private enum DesktopAccess : uint
        {
            DesktopNone = 0,
            DesktopReadobjects = 0x0001,
            DesktopCreatewindow = 0x0002,
            DesktopCreatemenu = 0x0004,
            DesktopHookcontrol = 0x0008,
            DesktopJournalrecord = 0x0010,
            DesktopJournalplayback = 0x0020,
            DesktopEnumerate = 0x0040,
            DesktopWriteobjects = 0x0080,
            DesktopSwitchdesktop = 0x0100,

            GenericAll = (DesktopReadobjects | DesktopCreatewindow | DesktopCreatemenu
                          | DesktopHookcontrol
                          | DesktopJournalrecord | DesktopJournalplayback |
                          DesktopEnumerate | DesktopWriteobjects | DesktopSwitchdesktop),
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        private const int NormalPriorityClass = 0x00000020;
    }
}
