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
        public IntPtr hWndFM;

        public SevenZipExtractorUsing7zFM(string filename)
        {
            Filename = filename;

            (PID, hWndFM) = RunFileManager(filename);
        }

        public void ExtractFile(string archiveEntryPath, string outputFolder)
        {
            var fullPath = Path.Combine(Filename, archiveEntryPath);

            NavigateToFilename(hWndFM, fullPath);

            var fmControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM);

            var listviewControl = fmControls.FirstOrDefault(c => c.ClassNN.Equals("SysListView321"));
            if (listviewControl == default) throw new Exception("Can't find main listview in 7-Zip File Manager window");

            //press F5 to extract the select file
            PostMessage(listviewControl.Handle, WindowMessage.WM_KEYDOWN, new IntPtr((int)VirtualKey.VK_F5), IntPtr.Zero);

            //wait for the "Extract to" prompt to appear
            IntPtr? hWndExtractWindow = null;
            while (true)
            {
                hWndExtractWindow = WindowHandleHelper.GetRootWindowByTitle(PID, title => title.Equals("Copy")); //todo: 7zFM displays different titles based on globalization settings. Perhaps search for windows that has specific controls

                if (hWndExtractWindow.HasValue)
                {
                    break;
                }

                Thread.Sleep(100);
            }

            var extractWindowControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndExtractWindow.Value);

            //set the Output Folder textbox
            var extractToFolderTextbox = extractWindowControls.FirstOrDefault(c => c.ClassNN.Equals("Edit1"));
            if (extractToFolderTextbox == default) throw new Exception("Can't find 'Export to folder' textbox");
            WindowHandleHelper.SetWindowText(extractToFolderTextbox.Handle, outputFolder);

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
        }


        static void NavigateToFilename(IntPtr hWndFM, string fullFilename)
        {
            var folder = Path.GetDirectoryName(fullFilename);
            if (folder == null) throw new Exception($"Could not derive folder path from: {fullFilename}");

            var fileName = Path.GetFileName(fullFilename);

            var fmControls = WindowHandleHelper.GetChildWindowsDetailsRecursively(hWndFM);
            var filenameTextbox = fmControls.FirstOrDefault(c => c.ClassNN.Equals("Edit1"));
            if (filenameTextbox == default) throw new Exception("Can't find the filename control in the 7-Zip File Manager window");

            //type the folder into the address bar
            WindowHandleHelper.SetWindowText(filenameTextbox.Handle, folder);
            PostMessage(filenameTextbox.Handle, WindowMessage.WM_KEYDOWN, new IntPtr((int)VirtualKey.VK_RETURN), IntPtr.Zero);

            var listviewControl = fmControls.FirstOrDefault(c => c.ClassNN.Equals("SysListView321"));
            if (listviewControl == default) throw new Exception("Can't find main listview in 7-Zip File Manager window");

            var lv = new ListviewNative(hWndFM, listviewControl.Handle);
            var rows = lv.GetRows();
            lv.SelectItemByText("Name", fileName);
        }

        static (int PID, IntPtr MainWindowHandle) RunFileManager(string filename)
        {
            var psi = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Normal,
                FileName = @"ext\7-Zip\win-x64\7zFM.exe",
                Arguments = $"\"{filename}\""
            };

            var proc = Process.Start(psi);

            if (proc == null) throw new Exception($"Could not start process: {psi.FileName} {psi.Arguments}");

            ChildProcessTracker.AddProcess(proc);

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

            //hide
            ShowWindow(hWndFM, SW_HIDE);

            return (proc.Id, hWndFM);
        }

        const int SW_HIDE = 0;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);


        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindow(IntPtr hWnd);

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
