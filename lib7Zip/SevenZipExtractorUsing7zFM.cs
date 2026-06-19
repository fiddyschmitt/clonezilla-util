using lib7Zip.UI;
using libCommon;
using libUIHelpers;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace lib7Zip
{
    public class SevenZipExtractorUsing7zFM
    {
        public string Filename { get; }

        public int PID;
        public IntPtr? desktopHandle;
        public IntPtr hWndFM;

        private readonly object usageLock = new();

        public SevenZipExtractorUsing7zFM(string filename)
        {
            Filename = filename;

            (PID, desktopHandle, var mainWindowHandle) = RunFileManager(filename, true);

            if (mainWindowHandle.HasValue)
            {
                hWndFM = mainWindowHandle.Value;
            }
        }

        const int MaxAttempts = 5;

        public void ExtractFile(string archiveEntryPath, string folder)
        {
            lock (usageLock)
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        var fullPath = Path.Combine(Filename, archiveEntryPath);

                        NavigateToFilename(PID, hWndFM, desktopHandle, fullPath);

                        var fmControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM);

                        var listviewControl = fmControls.FirstOrDefault(c => c.ClassNN.Equals("SysListView321"));
                        if (listviewControl == default) throw new Exception("Can't find main listview in 7-Zip File Manager window");

                        //press F5 to extract the select file
                        PInvoke.PostMessage((HWND)listviewControl.Handle, PInvoke.WM_KEYDOWN, (WPARAM)(nuint)VIRTUAL_KEY.VK_F5, (LPARAM)IntPtr.Zero);
                        PInvoke.PostMessage((HWND)listviewControl.Handle, PInvoke.WM_KEYUP, (WPARAM)(nuint)VIRTUAL_KEY.VK_F5, (LPARAM)IntPtr.Zero);

                        //wait for the "Extract to" prompt to appear
                        IntPtr? hWndExtractWindow = null;
                        var extractPromptDeadline = DateTime.Now + TimeSpan.FromSeconds(60);
                        while (true)
                        {
                            hWndExtractWindow = WindowHandleHelper.GetRootWindowByTitle(PID, desktopHandle, title => title.Equals("Copy")); //7zFM displays different titles based on globalization settings. Perhaps search for windows that has specific controls

                            if (hWndExtractWindow.HasValue)
                            {
                                break;
                            }

                            if (DateTime.Now > extractPromptDeadline)
                            {
                                throw new Exception("Timed out waiting for the Extract prompt to appear.");
                            }

                            Thread.Sleep(100);
                        }

                        var extractWindowControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndExtractWindow.Value);

                        //fill out the Output Folder textbox
                        var extractToFolderTextbox = extractWindowControls.FirstOrDefault(c => c.ClassNN.Equals("Edit1"));
                        if (extractToFolderTextbox == default) throw new Exception("Can't find 'Export to folder' textbox");
                        WindowHandleHelper.SetWindowText(extractToFolderTextbox.Handle, folder);

                        //press Enter
                        PInvoke.PostMessage((HWND)extractToFolderTextbox.Handle, PInvoke.WM_KEYDOWN, (WPARAM)(nuint)VIRTUAL_KEY.VK_RETURN, (LPARAM)IntPtr.Zero);
                        PInvoke.PostMessage((HWND)extractToFolderTextbox.Handle, PInvoke.WM_KEYUP, (WPARAM)(nuint)VIRTUAL_KEY.VK_RETURN, (LPARAM)IntPtr.Zero);

                        //Sending a mouse down even is susceptible to not working when the mouse is moved near the dialog. Not a problem when run on another desktop, but let's just go with keystrokes
                        /*
                        var okButton = extractWindowControls.FirstOrDefault(c => c.Text.Equals("OK"));
                        if (okButton == default) throw new Exception("Can't find 'OK' button");
                        SendMessage(okButton.Handle, WindowMessage.WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
                        SendMessage(okButton.Handle, WindowMessage.WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                        */

                        //wait for the extraction to finish
                        while (true)
                        {
                            bool windowExists = PInvoke.IsWindow((HWND)hWndExtractWindow.Value);
                            if (!windowExists)
                            {
                                break;
                            }

                            var errorDismissed = DismissErrorWindowIfPresent(PID, desktopHandle);
                            if (errorDismissed)
                            {
                                throw new Exception($"Error had to be dismissed during extraction.");
                            }

                            Thread.Sleep(100);
                        }

                        //wait for the file to be readable
                        while (true)
                        {
                            try
                            {
                                var extractedFilename = Directory.GetFiles(folder).First();
                                using var fs = File.OpenRead(extractedFilename);
                                break;
                            }
                            catch { }
                            Thread.Sleep(100);
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error while extracting {archiveEntryPath}: {ex}");

                        if (attempt == MaxAttempts)
                        {
                            throw new Exception($"Failed to extract {archiveEntryPath} after {MaxAttempts} attempts.", ex);
                        }

                        Log.Debug($"Retrying to extract {archiveEntryPath}");
                    }
                }
            }
        }


        static void NavigateToFilename(int pid, IntPtr hWndFM, IntPtr? desktopHandle, string fullFilename)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var folder = Path.GetDirectoryName(fullFilename) ?? throw new Exception($"Could not derive folder path from: {fullFilename}");
                    var fileName = Path.GetFileName(fullFilename);

                    var fmControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM);
                    var filenameTextbox = fmControls.FirstOrDefault(c => c.ClassNN.Equals("Edit1"));
                    if (filenameTextbox == default) throw new Exception("Can't find the filename control in the 7-Zip File Manager window");

                    //type the folder into the address bar
                    var currentPath = WindowHandleHelper.GetWindowText(filenameTextbox.Handle);

                    if (currentPath != null && currentPath.Equals($"{folder}\\", StringComparison.OrdinalIgnoreCase))
                    {
                        //we are already at the correct path
                    }
                    else
                    {
                        WindowHandleHelper.SetWindowText(filenameTextbox.Handle, folder);
                        PInvoke.PostMessage((HWND)filenameTextbox.Handle, PInvoke.WM_KEYDOWN, (WPARAM)(nuint)VIRTUAL_KEY.VK_RETURN, (LPARAM)IntPtr.Zero);
                        PInvoke.PostMessage((HWND)filenameTextbox.Handle, PInvoke.WM_KEYUP, (WPARAM)(nuint)VIRTUAL_KEY.VK_RETURN, (LPARAM)IntPtr.Zero);
                    }

                    var listviewControl = fmControls.FirstOrDefault(c => c.ClassNN.Equals("SysListView321"));
                    if (listviewControl == default) throw new Exception("Can't find main listview in 7-Zip File Manager window");

                    var lv = new ListviewNative(listviewControl.Handle);

                    //wait for rows to finish loading
                    while (true)
                    {
                        var rows = lv.GetRows();

                        var lastRow = rows.LastOrDefault();
                        if (lastRow != null && lastRow.TryGetValue("Name", out var lastRowFileName) && !string.IsNullOrEmpty(lastRowFileName))
                        {
                            break;
                        }

                        var errorDismissed = DismissErrorWindowIfPresent(pid, desktopHandle);
                        if (errorDismissed)
                        {
                            throw new Exception($"Error had to be dismissed during navigating to file.");
                        }

                        Thread.Sleep(100);
                    }

                    lv.SelectItemByText("Name", fileName);

                    return;
                }
                catch (Exception ex)
                {
                    Log.Error($"Error while navigating to {fullFilename}: {ex}");

                    if (attempt == MaxAttempts)
                    {
                        throw new Exception($"Failed to navigate to {fullFilename} after {MaxAttempts} attempts.", ex);
                    }

                    Log.Debug($"Retrying to navigate to {fullFilename}");
                }
            }
        }

        static bool DismissErrorWindowIfPresent(int pid, IntPtr? desktopHandle)
        {
            //If the VFS takes too long to service the 7-Zip File Manager, the following error gets displayed.
            var errorDismissed = false;
            WindowHandleHelper
                 .GetRootWindowsOfProcess(pid, desktopHandle)
                 .ForEach(rootWindow =>
                 {
                     var childHandleWithError = WindowHandleHelper.GetChildHandleByText(rootWindow,
                         text =>
                             text.Equals("Insufficient system resources exist to complete the requested service.") ||
                             text.Equals("The I/O operation has been aborted because of either a thread exit or an application request."));

                     if (childHandleWithError.HasValue && childHandleWithError != IntPtr.Zero)
                     {
                         //Dismiss the error
                         PInvoke.PostMessage((HWND)childHandleWithError.Value, PInvoke.WM_KEYDOWN, (WPARAM)(nuint)VIRTUAL_KEY.VK_RETURN, (LPARAM)IntPtr.Zero);
                         PInvoke.PostMessage((HWND)childHandleWithError.Value, PInvoke.WM_KEYUP, (WPARAM)(nuint)VIRTUAL_KEY.VK_RETURN, (LPARAM)IntPtr.Zero);
                         errorDismissed = true;
                     }
                 });

            return errorDismissed;
        }

        static (int PID, IntPtr? DesktopHandle, IntPtr? WindowHandle) RunFileManager(string filename, bool runOnSeperateDesktop)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Utility.Absolutify(@"ext\7-Zip\win-x64\7zFM.exe"),
                Arguments = $"\"{filename}\""
            };


            Process? proc;
            IntPtr? hWndFM;
            IntPtr? desktopHandle = null;
            if (runOnSeperateDesktop)
            {
                var expectedTitle = $"{filename}\\";
                var waitForMainWindow = new Func<(int Pid, IntPtr DesktopHandle), IntPtr>(pidDetails =>
                {
                    IntPtr? mainWindowHandle;
                    while (true)
                    {
                        mainWindowHandle = WindowHandleHelper.GetRootWindowByTitle(pidDetails.Pid, pidDetails.DesktopHandle, title => title.Equals(expectedTitle));

                        if (mainWindowHandle.HasValue) break;

                        Thread.Sleep(100);
                    }

                    return mainWindowHandle.Value;
                });

                var destkopName = Guid.NewGuid().ToString();    //unique desktops allow 7zFM to be run in parallel
                (var pid, desktopHandle, hWndFM) = DesktopUtility.RunProcessOnAnotherDesktop(psi, destkopName, waitForMainWindow);
                proc = Process.GetProcessById(pid);
            }
            else
            {
                proc = Process.Start(psi);

                if (proc == null) throw new Exception($"{nameof(proc)} is null");

                //This doesn't work when the process is started on another Desktop, or with (WindowStyle = Hidden and UseShellExecute = true).
                hWndFM = IntPtr.Zero;
                while (!proc.HasExited)
                {
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        hWndFM = proc.MainWindowHandle;
                        break;
                    }
                    Thread.Sleep(100);
                }

                //This does retrieve what it considers the main window, but it has no children so not useful
                /*
                var waitForMainWindow = new Func<(int Pid, IntPtr DesktopHandle), IntPtr>(pidDetails =>
                {
                    IntPtr mainWindowHandle;
                    while (true)
                    {
                        mainWindowHandle = ProcessUtility.GetMainWindowHandle(pidDetails.Pid, pidDetails.DesktopHandle);

                        if (mainWindowHandle != IntPtr.Zero) break;

                        Thread.Sleep(100);
                    }

                    return mainWindowHandle;
                });
                */
            }




            if (proc == null) throw new Exception($"Could not start process: {psi.FileName} {psi.Arguments}");
            ChildProcessTracker.AddProcess(proc);

            if (hWndFM == IntPtr.Zero) throw new Exception("Can't find 7-Zip File Manager window");

            //Useful for diagnosing issues
            /*
            var rootWindows = WindowHandleHelper.GetRootWindowsOfProcess(proc.Id, desktopHandle)
                                .Select(window => new WinHandle()
                                {
                                    Handle = window,
                                    Text = WindowHandleHelper.GetWindowText(window)
                                })
                                .ToList();

            _ = rootWindows
                    .Recurse(parent =>
                    {
                        var childWindows = WindowHandleHelper
                                            .GetChildWindows(parent.Handle)
                                            .Select(child => new WinHandle()
                                            {
                                                Handle = child,
                                                Text = WindowHandleHelper.GetWindowText(child)
                                            })
                                            .ToList();

                        parent.Children.AddRange(childWindows);

                        return childWindows;
                    })
                    .ToList();
            */

            //var childWindows = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM);

            //childWindows = WindowHandleHelper.GetChildWindowsDetailsRecursively(rootWindows);

            //ShowWindow(hWndFM, SW_HIDE);

            return (proc.Id, desktopHandle, hWndFM);
        }
    }
}
