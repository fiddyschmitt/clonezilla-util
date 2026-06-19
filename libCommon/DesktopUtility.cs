using libUIHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;

namespace libCommon
{
    public static class DesktopUtility
    {
        static readonly List<IntPtr> DesktopsCreated = [];

        public static void Cleanup()
        {
            lock (DesktopsCreated)
            {
                DesktopsCreated
                    .ForEach(deskopHandle =>
                    {
                        try
                        {
                            PInvoke.CloseDesktop((HDESK)deskopHandle);
                        }
                        catch { }
                    });
            }
        }

        public static unsafe (int pid, IntPtr DesktopHandle, IntPtr? WindowHandle) RunProcessOnAnotherDesktop(ProcessStartInfo psi, string desktopName, Func<(int pid, IntPtr DesktopHandle), IntPtr>? waitForWindow)
        {
            //Consider checking if the desktop already exists. Perhaps use EnumDesktop and OpenDesktop
            IntPtr hNewDesktop;
            fixed (char* desktopNamePtr = desktopName)
            {
                hNewDesktop = PInvoke.CreateDesktop(desktopNamePtr, default, null, default, (uint)DesktopAccess.GenericAll, null);
            }

            lock (DesktopsCreated)
            {
                DesktopsCreated.Add(hNewDesktop);
            }

            var command = $"\"{psi.FileName}\" {psi.Arguments}";

            // REVERT NOTE: This was migrated from a hand-written ANSI CreateProcess to CsWin32's
            // Unicode CreateProcessW (CsWin32 only exposes the W variant). The previous code carried
            // a comment that CharSet.Unicode "causes 7zFM.exe not to run. Unsure why" - that was most
            // likely a struct CharSet mismatch in the old attempt, which the correctly-marshalled
            // STARTUPINFOW below avoids. If launching 7zFM on a separate desktop regresses, revert
            // this block to the previous ANSI [DllImport] CreateProcess + managed STARTUPINFO.
            Span<char> commandBuffer = stackalloc char[command.Length + 1];
            command.AsSpan().CopyTo(commandBuffer);
            commandBuffer[command.Length] = '\0';

            PROCESS_INFORMATION pi;
            fixed (char* desktopPtr = desktopName)
            {
                var si = new STARTUPINFOW
                {
                    cb = (uint)sizeof(STARTUPINFOW),
                    lpDesktop = desktopPtr
                };

                // start the process.
                PInvoke.CreateProcess(
                    null,
                    ref commandBuffer,
                    null,
                    null,
                    true,
                    PROCESS_CREATION_FLAGS.NORMAL_PRIORITY_CLASS,
                    null,
                    null,
                    si,
                    out pi);
            }

            IntPtr? windowToReturn = waitForWindow?.Invoke(((int)pi.dwProcessId, hNewDesktop));

            return ((int)pi.dwProcessId, hNewDesktop, windowToReturn);
        }

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
    }
}