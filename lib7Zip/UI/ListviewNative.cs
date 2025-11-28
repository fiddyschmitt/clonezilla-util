using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace lib7Zip.UI
{
    public class ListviewNative
    {
        private readonly IntPtr hListview;

        public ListviewNative(IntPtr hListview)
        {
            this.hListview = hListview;
        }

        public int GetColumnCount()
        {
            var columnCount = HeaderHelper.GetColumnCount(hListview);

            return columnCount;
        }

        public int GetRowCount()
        {
            //var result = int.Parse(AutoIt.AutoItX.ControlListView(hWnd, hListview, "GetItemCount", null, null));
            int lvCount = (int)WinAPI.SendMessage(hListview, SelectHelper.LVM_GETITEMCOUNT, 0, IntPtr.Zero);
            return lvCount;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);


        public List<string> GetColumnNames()
        {
            var colCount = GetColumnCount();

            _ = GetWindowThreadProcessId(hListview, out uint pid);

            var result = Enumerable.Range(0, colCount)
                            .Select(colIndex => HeaderHelper.GetListViewColumn(hListview, pid, colIndex) ?? "Unknown")
                            .ToList();

            return result;
        }

        public List<List<string>> GetRowData()
        {
            var colCount = GetColumnCount();
            var rowCount = GetRowCount();

            var result = Enumerable
                            .Range(0, rowCount)
                            .Select(rowIndex =>
                            {
                                var row = Enumerable
                                            .Range(0, colCount)
                                            //.Select(colIndex => AutoIt.AutoItX.ControlListView(hWnd, hListview, "GetText", "" + rowIndex, "" + colIndex))
                                            .Select(colIndex => GetRowText(rowIndex, colIndex))
                                            .ToList();
                                return row;
                            })
                            .ToList();

            return result;
        }

        public string GetRowText(int rowIndex, int columnIndex)
        {
            // get the ID of the process who owns the list view
            uint processId = 0;
            _ = WinAPI.GetWindowThreadProcessId(hListview, ref processId);

            // open the process
            var processHandle = WinAPI.OpenProcess(
                WinAPI.ProcessAccessFlags.VirtualMemoryOperation
                | WinAPI.ProcessAccessFlags.VirtualMemoryRead
                | WinAPI.ProcessAccessFlags.VirtualMemoryWrite,
                false,
                processId);

            // allocate buffer for a string to store the text of the list view item we wanted
            var textBufferPtr = WinAPI.VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                WinAPI.MAX_LVMSTRING,
                WinAPI.AllocationType.Commit,
                WinAPI.MemoryProtection.ReadWrite);

            // this is the LVITEM we need to inject
            var lvItem = new WinAPI.LVITEM
            {
                mask = (uint)WinAPI.ListViewItemFilters.LVIF_TEXT,
                cchTextMax = (int)WinAPI.MAX_LVMSTRING,
                pszText = textBufferPtr,
                iItem = rowIndex,
                iSubItem = columnIndex
            };

            // allocate memory for the LVITEM structure in the remote process
            var lvItemSize = Marshal.SizeOf(lvItem);
            var lvItemBufferPtr = WinAPI.VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (uint)lvItemSize,
                WinAPI.AllocationType.Commit,
                WinAPI.MemoryProtection.ReadWrite);

            // to inject the LVITEM structure, we have to use the WriteProcessMemory API, which does a pointer-to-pointer copy. So we need to turn the managed LVITEM structure to an unmanaged LVITEM pointer
            // first allocate a piece of unmanaged memory ...
            var lvItemLocalPtr = Marshal.AllocHGlobal(lvItemSize);

            // ... then copy the managed object into the unmanaged memory
            Marshal.StructureToPtr(lvItem, lvItemLocalPtr, false);

            // and write into remote process's memory
            WinAPI.WriteProcessMemory(
                processHandle,
                lvItemBufferPtr,
                lvItemLocalPtr,
                (uint)lvItemSize,
                out var _);

            // tell the list view to fill in the text we desired
            WinAPI.SendMessage(hListview, (int)WinAPI.ListViewMessages.LVM_GETITEMTEXT, rowIndex, lvItemBufferPtr);

            // read the text. we allocate a managed byte array to store the retrieved text instead of AllocHGlobal-ing a piece of unmanaged memory, because CLR knows how to marshal between a pointer and a byte array
            var localTextBuffer = new byte[WinAPI.MAX_LVMSTRING];
            WinAPI.ReadProcessMemory(
                processHandle,
                textBufferPtr,
                localTextBuffer,
                (int)WinAPI.MAX_LVMSTRING,
                out var _);

            // convert the byte array to a string. assume the remote process uses Unicode
            var text = Encoding.Unicode.GetString(localTextBuffer).TrimEnd(['\0', '\uFFFD']);

            // finally free all the memory we allocated, and close the process handle we opened
            WinAPI.VirtualFreeEx(processHandle, textBufferPtr, 0, WinAPI.AllocationType.Release);
            WinAPI.VirtualFreeEx(processHandle, lvItemBufferPtr, 0, WinAPI.AllocationType.Release);
            Marshal.FreeHGlobal(lvItemLocalPtr);

            WinAPI.CloseHandle(processHandle);

            return text;
        }

        public void SelectItemByText(string columnName, string itemText)
        {
            var rows = GetRows();

            var item = rows
                        .Select((row, index) => new
                        {
                            Index = index,
                            Row = row
                        })
                        .FirstOrDefault(row => row.Row[columnName].Equals(itemText)) ?? throw new Exception($"Could not find item with text: {itemText}");

            SelectItemByIndex(item.Index);
        }

        public void SelectItemByIndex(int itemIndex)
        {
            //This doesn't focus on the item
            //AutoIt.AutoItX.ControlListView(hWnd, hListview, "Select", "" + itemIndex, null);
            _ = GetWindowThreadProcessId(hListview, out uint pid);
            SelectHelper.SelectItem(hListview, pid, itemIndex);
        }

        public List<Dictionary<string, string>> GetRows()
        {
            var colNames = GetColumnNames();

            var rows = GetRowData();

            var result = rows
                            .Select(row => colNames
                                            .Zip(row, (colName, cellValue) => new
                                            {
                                                ColumnName = colName,
                                                CellValue = cellValue
                                            })
                                            .ToDictionary(t => t.ColumnName, t => t.CellValue)
                            )
                            .ToList();

            return result;
        }
    }

    public static class HeaderHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct LV_ITEM
        {
            public int mask;
            public int iItem;
            public int iSubItem;
            public int state;
            public int stateMask;
            public char* pszText;
            public int cchTextMax;
            public int iImage;
            public int lParam;
            public int iIndent;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LV_COLUMN
        {
            public int mask;
            public int fmt;
            public int cx;
            public IntPtr pszText;
            public int cchTextMax;
            public int iSubItem;
            public int iImage;
            private int iOrder;

            public int IOrder { readonly get => iOrder; set => iOrder = value; }
        }

        [Flags()]
        public enum Win32ProcessAccessType
        {
            AllAccess = CreateThread | DuplicateHandle | QueryInformation | SetInformation | Terminate | VMOperation | VMRead | VMWrite | Synchronize,
            CreateThread = 0x2,
            DuplicateHandle = 0x40,
            QueryInformation = 0x400,
            SetInformation = 0x200,
            Terminate = 0x1,
            VMOperation = 0x8,
            VMRead = 0x10,
            VMWrite = 0x20,
            Synchronize = 0x100000
        }

        [Flags]
        public enum Win32AllocationTypes
        {
            MEM_COMMIT = 0x1000,
            MEM_RESERVE = 0x2000,
            MEM_DECOMMIT = 0x4000,
            MEM_RELEASE = 0x8000,
            MEM_RESET = 0x80000,
            MEM_PHYSICAL = 0x400000,
            MEM_TOP_DOWN = 0x100000,
            WriteWatch = 0x200000,
            MEM_LARGE_PAGES = 0x20000000
        }

        [Flags]
        public enum Win32MemoryProtection
        {
            PAGE_EXECUTE = 0x10,
            PAGE_EXECUTE_READ = 0x20,
            PAGE_EXECUTE_READWRITE = 0x40,
            PAGE_EXECUTE_WRITECOPY = 0x80,
            PAGE_NOACCESS = 0x01,
            PAGE_READONLY = 0x02,
            PAGE_READWRITE = 0x04,
            PAGE_WRITECOPY = 0x08,
            PAGE_GUARD = 0x100,
            PAGE_NOCACHE = 0x200,
            PAGE_WRITECOMBINE = 0x400
        }

        [DllImport("kernel32.dll")]
        internal static extern IntPtr OpenProcess(Win32ProcessAccessType dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, Win32AllocationTypes flWin32AllocationType, Win32MemoryProtection flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        //internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref LV_ITEM lpBuffer, uint nSize, out int lpNumberOfBytesWritten);
        internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref LV_COLUMN lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, int dwSize, out int lpNumberOfBytesRead);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, Win32AllocationTypes dwFreeType);

        [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        const int LVM_FIRST = 0x1000;
        const int LVM_GETHEADER = LVM_FIRST + 31;

        const int HDM_GETITEMCOUNT = 0x1200;

        public static int GetColumnCount(IntPtr hListview)
        {
            var hWndHeader = SendMessage(hListview, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            var columnCount = (int)SendMessage(hWndHeader, HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

            return columnCount;
        }

        public static string? GetListViewColumn(IntPtr hwnd, uint processId, int Column)
        {
            const int dwBufferSize = 2048;
            const int LVM_FIRST = 0x1000;
            //const int LVM_GETCOLUMNA = LVM_FIRST + 25;
            const int LVM_GETCOLUMNW = LVM_FIRST + 95;
            //const int LVCF_FMT = 0x00000001;
            const int LVCF_TEXT = 0x00000004;

            LV_COLUMN lvCol;
            string? retval;
            bool bSuccess;
            IntPtr hProcess = IntPtr.Zero;
            IntPtr lpRemoteBuffer = IntPtr.Zero;
            IntPtr lpLocalBuffer = IntPtr.Zero;

            try
            {
                lvCol = new LV_COLUMN();
                lpLocalBuffer = Marshal.AllocHGlobal(dwBufferSize);
                hProcess = OpenProcess(Win32ProcessAccessType.AllAccess, false, processId);
                if (hProcess == IntPtr.Zero)
                    throw new ApplicationException("Failed to access process!");

                lpRemoteBuffer = VirtualAllocEx(hProcess, System.IntPtr.Zero, dwBufferSize, Win32AllocationTypes.MEM_COMMIT, Win32MemoryProtection.PAGE_READWRITE);
                if (lpRemoteBuffer == IntPtr.Zero)
                    throw new System.SystemException("Failed to allocate memory in remote process");

                lvCol.mask = LVCF_TEXT;
                lvCol.pszText = (IntPtr)(lpRemoteBuffer.ToInt64() + Marshal.SizeOf(typeof(LV_COLUMN)));
                lvCol.cchTextMax = 500;
                lvCol.IOrder = Column;
                lvCol.iSubItem = Column;

                bSuccess = WriteProcessMemory(hProcess, lpRemoteBuffer, ref lvCol, (uint)Marshal.SizeOf(typeof(LV_COLUMN)), out _);
                if (!bSuccess)
                    throw new SystemException("Failed to write to process memory");

                SendMessage(hwnd, LVM_GETCOLUMNW, (IntPtr)Column, lpRemoteBuffer);

                bSuccess = ReadProcessMemory(hProcess, lpRemoteBuffer, lpLocalBuffer, dwBufferSize, out _);

                if (!bSuccess)
                    throw new SystemException("Failed to read from process memory");

                retval = Marshal.PtrToStringUni((IntPtr)(lpLocalBuffer.ToInt64() + Marshal.SizeOf(typeof(LV_COLUMN))));
            }
            finally
            {
                if (lpLocalBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(lpLocalBuffer);
                if (lpRemoteBuffer != IntPtr.Zero)
                    VirtualFreeEx(hProcess, lpRemoteBuffer, 0, Win32AllocationTypes.MEM_RELEASE);
                if (hProcess != IntPtr.Zero)
                    CloseHandle(hProcess);
            }

            return retval;
        }
    }

    public static class SelectHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct LV_ITEM
        {
            public int mask;
            public int iItem;
            public int iSubItem;
            public int state;
            public int stateMask;
            public char* pszText;
            public int cchTextMax;
            public int iImage;
            public int lParam;
            public int iIndent;
        }

        [Flags()]
        public enum Win32ProcessAccessType
        {
            AllAccess = CreateThread | DuplicateHandle | QueryInformation | SetInformation | Terminate | VMOperation | VMRead | VMWrite | Synchronize,
            CreateThread = 0x2,
            DuplicateHandle = 0x40,
            QueryInformation = 0x400,
            SetInformation = 0x200,
            Terminate = 0x1,
            VMOperation = 0x8,
            VMRead = 0x10,
            VMWrite = 0x20,
            Synchronize = 0x100000
        }

        [Flags]
        public enum Win32AllocationTypes
        {
            MEM_COMMIT = 0x1000,
            MEM_RESERVE = 0x2000,
            MEM_DECOMMIT = 0x4000,
            MEM_RELEASE = 0x8000,
            MEM_RESET = 0x80000,
            MEM_PHYSICAL = 0x400000,
            MEM_TOP_DOWN = 0x100000,
            WriteWatch = 0x200000,
            MEM_LARGE_PAGES = 0x20000000
        }

        [Flags]
        public enum Win32MemoryProtection
        {
            PAGE_EXECUTE = 0x10,
            PAGE_EXECUTE_READ = 0x20,
            PAGE_EXECUTE_READWRITE = 0x40,
            PAGE_EXECUTE_WRITECOPY = 0x80,
            PAGE_NOACCESS = 0x01,
            PAGE_READONLY = 0x02,
            PAGE_READWRITE = 0x04,
            PAGE_WRITECOPY = 0x08,
            PAGE_GUARD = 0x100,
            PAGE_NOCACHE = 0x200,
            PAGE_WRITECOMBINE = 0x400
        }

        [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr OpenProcess(Win32ProcessAccessType dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, Win32AllocationTypes flWin32AllocationType, Win32MemoryProtection flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref LV_ITEM lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, Win32AllocationTypes dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        public const int LVM_FIRST = 0x1000;
        public const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
        public const int LVIF_STATE = 0x00000008;
        public const int LVIS_FOCUSED = 0x0001;
        public const int LVIS_SELECTED = 0x0002;
        public const int LVM_SETITEMSTATE = (LVM_FIRST + 43);
        public const int LVM_ENSUREVISIBLE = (LVM_FIRST + 19);

        public static void SelectItem(IntPtr hListview, uint processId, int itemIndex)
        {
            int lvCount = (int)SendMessage(hListview, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);

            if (lvCount > 0)
            {
                IntPtr processHandle = IntPtr.Zero;
                IntPtr lvMemItem;
                LV_ITEM lvLocalItem = new();

                try
                {
                    processHandle = OpenProcess(Win32ProcessAccessType.AllAccess, false, processId);
                    if (processHandle == IntPtr.Zero)
                        throw new ApplicationException("Failed to access process!");

                    //ListViewItem
                    lvLocalItem.mask = LVIF_STATE;
                    lvLocalItem.stateMask = LVIS_SELECTED;
                    lvLocalItem.state = 0;

                    lvMemItem = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)Marshal.SizeOf(lvLocalItem), Win32AllocationTypes.MEM_COMMIT, Win32MemoryProtection.PAGE_READWRITE);
                    if (lvMemItem == IntPtr.Zero)
                        throw new SystemException("Failed to allocate memory in remote process");

                    lvMemItem = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)Marshal.SizeOf(lvLocalItem), Win32AllocationTypes.MEM_COMMIT, Win32MemoryProtection.PAGE_READWRITE); // alloc memory for my whole ListviewItem

                    WriteProcessMemory(processHandle, lvMemItem, ref lvLocalItem, (uint)Marshal.SizeOf(lvLocalItem), out int tmpOut);

                    for (int i = 0; i < lvCount; i++) // unhighlight all
                    {
                        SendMessage(hListview, LVM_SETITEMSTATE, (IntPtr)i, lvMemItem);
                    }


                    lvLocalItem.state = LVIS_SELECTED | LVIS_FOCUSED;
                    lvLocalItem.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
                    lvMemItem = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)Marshal.SizeOf(lvLocalItem), Win32AllocationTypes.MEM_COMMIT, Win32MemoryProtection.PAGE_READWRITE);
                    WriteProcessMemory(processHandle, lvMemItem, ref lvLocalItem, (uint)Marshal.SizeOf(lvLocalItem), out tmpOut);
                    SendMessage(hListview, LVM_SETITEMSTATE, (IntPtr)itemIndex, lvMemItem);
                    SendMessage(hListview, LVM_ENSUREVISIBLE, (IntPtr)itemIndex, IntPtr.Zero);
                }
                finally
                {
                    if (processHandle != IntPtr.Zero)
                        CloseHandle(processHandle);
                }

            }//end if
        }
    }
}
