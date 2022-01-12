using lib7Zip.UI;
using libCommon;
using libUIHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static PInvoke.User32;

namespace lib7Zip
{
    public class SevenZipExtractorUsing7zFM
    {
        public string Filename { get; }

        public int PID;
        public IntPtr? desktopHandle;
        public IntPtr hWndFM;

        public SevenZipExtractorUsing7zFM(string filename)
        {
            Filename = filename;

            (PID, desktopHandle, hWndFM) = RunFileManager(filename);
        }

        public void ExtractFile(string archiveEntryPath, string folder)
        {
            var fullPath = Path.Combine(Filename, archiveEntryPath);

            NavigateToFilename(hWndFM, desktopHandle, fullPath);

            var fmControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM, desktopHandle);

            var listviewControl = fmControls.FirstOrDefault(c => c.ClassNN.Equals("SysListView321"));
            if (listviewControl == default) throw new Exception("Can't find main listview in 7-Zip File Manager window");

            //press F5 to extract the select file
            PostMessage(listviewControl.Handle, WindowMessage.WM_KEYDOWN, new IntPtr((int)VirtualKey.VK_F5), IntPtr.Zero);

            //wait for the "Extract to" prompt to appear
            IntPtr? hWndExtractWindow = null;
            while (true)
            {
                hWndExtractWindow = WindowHandleHelper.GetRootWindowByTitle(PID, desktopHandle, title => title.Equals("Copy")); //todo: 7zFM displays different titles based on globalization settings. Perhaps search for windows that has specific controls

                if (hWndExtractWindow.HasValue)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            var extractWindowControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndExtractWindow.Value, desktopHandle);

            //set the Output Folder textbox
            var extractToFolderTextbox = extractWindowControls.FirstOrDefault(c => c.ClassNN.Equals("Edit1"));
            if (extractToFolderTextbox == default) throw new Exception("Can't find 'Export to folder' textbox");
            WindowHandleHelper.SetWindowText(extractToFolderTextbox.Handle, folder);

            var okButton = extractWindowControls.FirstOrDefault(c => c.Text.Equals("OK"));
            if (okButton == default) throw new Exception("Can't find 'OK' button");

            SendMessage(okButton.Handle, WindowMessage.WM_LBUTTONDOWN, IntPtr.Zero, IntPtr.Zero);
            SendMessage(okButton.Handle, WindowMessage.WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);

            //wait for the extraction to finish
            while (true)
            {
                var windowExists = IsWindow(hWndExtractWindow.Value);
                if (!windowExists)
                {
                    break;
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
        }


        static void NavigateToFilename(IntPtr hWndFM, IntPtr? desktopHandle, string fullFilename)
        {
            var folder = Path.GetDirectoryName(fullFilename);
            if (folder == null) throw new Exception($"Could not derive folder path from: {fullFilename}");

            var fileName = Path.GetFileName(fullFilename);

            var fmControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM, desktopHandle);
            var filenameTextbox = fmControls.FirstOrDefault(c => c.ClassNN.Equals("Edit1"));
            if (filenameTextbox == default) throw new Exception("Can't find the filename control in the 7-Zip File Manager window");

            //type the folder into the address bar
            WindowHandleHelper.SetWindowText(filenameTextbox.Handle, folder);
            PostMessage(filenameTextbox.Handle, WindowMessage.WM_KEYDOWN, new IntPtr((int)VirtualKey.VK_RETURN), IntPtr.Zero);

            var listviewControl = fmControls.FirstOrDefault(c => c.ClassNN.Equals("SysListView321"));
            if (listviewControl == default) throw new Exception("Can't find main listview in 7-Zip File Manager window");

            var lv = new ListviewNative(hWndFM, listviewControl.Handle);

            //wait for rows to finish loading
            while (true)
            {
                var rows = lv.GetRows();

                var lastRowFileName = rows.Last()["Name"];
                if (!string.IsNullOrEmpty(lastRowFileName))
                {
                    break;
                }

                Thread.Sleep(100);
            }

            lv.SelectItemByText("Name", fileName);
        }

        static (int PID, IntPtr? DesktopHandle, IntPtr MainWindowHandle) RunFileManager(string filename)
        {
            var psi = new ProcessStartInfo
            {                               
                FileName = Path.GetFullPath(@"ext\7-Zip\win-x64\7zFM.exe"),
                Arguments = $"\"{filename}\""
            };

            var proc = Process.Start(psi);
            IntPtr? desktopHandle = null;

            //Not quite working yet. Can run the process in another Desktop, get its Pid and MainDesktopHandle, can even get all top level windows using EnumDesktopWindows. However can't retrieve the child windows (required to control)
            //(var pid, var desktopHandle, var hWndFM) = DesktopUtility.RunProcessOnAnotherDesktop(psi, "Narnia");
            //var proc = Process.GetProcessById(pid);            

            if (proc == null) throw new Exception($"Could not start process: {psi.FileName} {psi.Arguments}");
            ChildProcessTracker.AddProcess(proc);


            //This doesn't work when the process is started on another Desktop, or with (WindowStyle = Hidden and UseShellExecute = true).
            IntPtr hWndFM = IntPtr.Zero;
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

            if (hWndFM == IntPtr.Zero) throw new Exception("Can't find 7-Zip File Manager window");

            //ShowWindow(hWndFM, SW_HIDE);

            return (proc.Id, desktopHandle, hWndFM);
        }

        

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);


        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindow(IntPtr hWnd);

        const int SW_HIDE = 0;

        [DllImport("user32.dll")]
        private static extern int ShowWindow(IntPtr hwnd, int nCmdShow);

        static IntPtr FindWindowByIndex(IntPtr hWndParent, string controlClass, int index)
        {
            if (index == 0)
                return hWndParent;
            else
            {
                int ct = 0;
                IntPtr result = IntPtr.Zero;
                do
                {
                    result = FindWindowEx(hWndParent, result, controlClass, null);
                    if (result != IntPtr.Zero)
                        ++ct;
                }
                while (ct < index && result != IntPtr.Zero);
                return result;
            }
        }
    }
}
