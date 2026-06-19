using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;

namespace lib7Zip.UI
{
    public class ListviewNative
    {
        const uint LVM_GETITEMTEXT = 0x104B;
        const uint LVIF_TEXT = 0x0001;
        const uint MAX_LVMSTRING = 255;

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
            int lvCount = (int)(nint)PInvoke.SendMessage((HWND)hListview, SelectHelper.LVM_GETITEMCOUNT, default, default);
            return lvCount;
        }

        public List<string> GetColumnNames()
        {
            var colCount = GetColumnCount();

            _ = PInvoke.GetWindowThreadProcessId((HWND)hListview, out uint pid);

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

        public unsafe string GetRowText(int rowIndex, int columnIndex)
        {
            // get the ID of the process who owns the list view
            _ = PInvoke.GetWindowThreadProcessId((HWND)hListview, out uint processId);

            // open the process
            HANDLE processHandle = PInvoke.OpenProcess(
                PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION
                | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ
                | PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE,
                false,
                processId);

            // allocate buffer for a string to store the text of the list view item we wanted
            var textBufferPtr = PInvoke.VirtualAllocEx(
                processHandle,
                null,
                MAX_LVMSTRING,
                VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT,
                PAGE_PROTECTION_FLAGS.PAGE_READWRITE);

            // this is the LVITEM we need to inject
            var lvItem = new LVITEM
            {
                mask = LVIF_TEXT,
                cchTextMax = (int)MAX_LVMSTRING,
                pszText = (IntPtr)textBufferPtr,
                iItem = rowIndex,
                iSubItem = columnIndex
            };

            // allocate memory for the LVITEM structure in the remote process
            var lvItemSize = Marshal.SizeOf(lvItem);
            var lvItemBufferPtr = PInvoke.VirtualAllocEx(
                processHandle,
                null,
                (nuint)lvItemSize,
                VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT,
                PAGE_PROTECTION_FLAGS.PAGE_READWRITE);

            // to inject the LVITEM structure, we have to use the WriteProcessMemory API, which does a pointer-to-pointer copy. So we need to turn the managed LVITEM structure to an unmanaged LVITEM pointer
            // first allocate a piece of unmanaged memory ...
            var lvItemLocalPtr = Marshal.AllocHGlobal(lvItemSize);

            // ... then copy the managed object into the unmanaged memory
            Marshal.StructureToPtr(lvItem, lvItemLocalPtr, false);

            // and write into remote process's memory
            PInvoke.WriteProcessMemory(
                processHandle,
                lvItemBufferPtr,
                (void*)lvItemLocalPtr,
                (nuint)lvItemSize,
                null);

            // tell the list view to fill in the text we desired
            PInvoke.SendMessage((HWND)hListview, LVM_GETITEMTEXT, (WPARAM)(nuint)rowIndex, (LPARAM)(nint)lvItemBufferPtr);

            // read the text. we allocate a managed byte array to store the retrieved text instead of AllocHGlobal-ing a piece of unmanaged memory, because CLR knows how to marshal between a pointer and a byte array
            var localTextBuffer = new byte[MAX_LVMSTRING];
            fixed (byte* localTextBufferPtr = localTextBuffer)
            {
                PInvoke.ReadProcessMemory(
                    processHandle,
                    textBufferPtr,
                    localTextBufferPtr,
                    MAX_LVMSTRING,
                    null);
            }

            // convert the byte array to a string. assume the remote process uses Unicode
            var text = Encoding.Unicode.GetString(localTextBuffer).TrimEnd(['\0', (char)0xFFFD]);

            // finally free all the memory we allocated, and close the process handle we opened
            PInvoke.VirtualFreeEx(processHandle, textBufferPtr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
            PInvoke.VirtualFreeEx(processHandle, lvItemBufferPtr, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
            Marshal.FreeHGlobal(lvItemLocalPtr);

            PInvoke.CloseHandle(processHandle);

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
            _ = PInvoke.GetWindowThreadProcessId((HWND)hListview, out uint pid);
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

        [StructLayout(LayoutKind.Sequential)]
        struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
        }
    }

    public static class HeaderHelper
    {
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

        const uint LVM_FIRST = 0x1000;
        const uint LVM_GETHEADER = LVM_FIRST + 31;

        const uint HDM_GETITEMCOUNT = 0x1200;

        public static int GetColumnCount(IntPtr hListview)
        {
            nint hWndHeader = PInvoke.SendMessage((HWND)hListview, LVM_GETHEADER, default, default);
            var columnCount = (int)(nint)PInvoke.SendMessage((HWND)hWndHeader, HDM_GETITEMCOUNT, default, default);

            return columnCount;
        }

        public static unsafe string? GetListViewColumn(IntPtr hwnd, uint processId, int Column)
        {
            const int dwBufferSize = 2048;
            const uint LVM_FIRST = 0x1000;
            //const int LVM_GETCOLUMNA = LVM_FIRST + 25;
            const uint LVM_GETCOLUMNW = LVM_FIRST + 95;
            //const int LVCF_FMT = 0x00000001;
            const int LVCF_TEXT = 0x00000004;

            LV_COLUMN lvCol;
            string? retval;
            bool bSuccess;
            HANDLE hProcess = default;
            void* lpRemoteBuffer = null;
            IntPtr lpLocalBuffer = IntPtr.Zero;

            try
            {
                lvCol = new LV_COLUMN();
                lpLocalBuffer = Marshal.AllocHGlobal(dwBufferSize);
                hProcess = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_ALL_ACCESS, false, processId);
                if (hProcess == default)
                    throw new ApplicationException("Failed to access process!");

                lpRemoteBuffer = PInvoke.VirtualAllocEx(hProcess, null, dwBufferSize, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                if (lpRemoteBuffer == null)
                    throw new System.SystemException("Failed to allocate memory in remote process");

                lvCol.mask = LVCF_TEXT;
                lvCol.pszText = (IntPtr)((byte*)lpRemoteBuffer + Marshal.SizeOf<LV_COLUMN>());
                lvCol.cchTextMax = 500;
                lvCol.IOrder = Column;
                lvCol.iSubItem = Column;

                bSuccess = PInvoke.WriteProcessMemory(hProcess, lpRemoteBuffer, &lvCol, (nuint)Marshal.SizeOf<LV_COLUMN>(), null);
                if (!bSuccess)
                    throw new SystemException("Failed to write to process memory");

                PInvoke.SendMessage((HWND)hwnd, LVM_GETCOLUMNW, (WPARAM)(nuint)Column, (LPARAM)(nint)lpRemoteBuffer);

                bSuccess = PInvoke.ReadProcessMemory(hProcess, lpRemoteBuffer, (void*)lpLocalBuffer, dwBufferSize, null);

                if (!bSuccess)
                    throw new SystemException("Failed to read from process memory");

                retval = Marshal.PtrToStringUni((IntPtr)((byte*)lpLocalBuffer + Marshal.SizeOf<LV_COLUMN>()));
            }
            finally
            {
                if (lpLocalBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(lpLocalBuffer);
                if (lpRemoteBuffer != null && hProcess != default)
                    PInvoke.VirtualFreeEx(hProcess, lpRemoteBuffer, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
                if (hProcess != default)
                    PInvoke.CloseHandle(hProcess);
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

        public const uint LVM_FIRST = 0x1000;
        public const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
        public const int LVIF_STATE = 0x00000008;
        public const int LVIS_FOCUSED = 0x0001;
        public const int LVIS_SELECTED = 0x0002;
        public const uint LVM_SETITEMSTATE = (LVM_FIRST + 43);
        public const uint LVM_ENSUREVISIBLE = (LVM_FIRST + 19);

        public static unsafe void SelectItem(IntPtr hListview, uint processId, int itemIndex)
        {
            int lvCount = (int)(nint)PInvoke.SendMessage((HWND)hListview, LVM_GETITEMCOUNT, default, default);

            if (lvCount > 0)
            {
                HANDLE processHandle = default;
                void* lvMemItem;
                LV_ITEM lvLocalItem = new();

                try
                {
                    processHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_ALL_ACCESS, false, processId);
                    if (processHandle == default)
                        throw new ApplicationException("Failed to access process!");

                    //ListViewItem
                    lvLocalItem.mask = LVIF_STATE;
                    lvLocalItem.stateMask = LVIS_SELECTED;
                    lvLocalItem.state = 0;

                    lvMemItem = PInvoke.VirtualAllocEx(processHandle, null, (nuint)Marshal.SizeOf(lvLocalItem), VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                    if (lvMemItem == null)
                        throw new SystemException("Failed to allocate memory in remote process");

                    lvMemItem = PInvoke.VirtualAllocEx(processHandle, null, (nuint)Marshal.SizeOf(lvLocalItem), VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE); // alloc memory for my whole ListviewItem

                    PInvoke.WriteProcessMemory(processHandle, lvMemItem, &lvLocalItem, (nuint)Marshal.SizeOf(lvLocalItem), null);

                    for (int i = 0; i < lvCount; i++) // unhighlight all
                    {
                        PInvoke.SendMessage((HWND)hListview, LVM_SETITEMSTATE, (WPARAM)(nuint)i, (LPARAM)(nint)lvMemItem);
                    }


                    lvLocalItem.state = LVIS_SELECTED | LVIS_FOCUSED;
                    lvLocalItem.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
                    lvMemItem = PInvoke.VirtualAllocEx(processHandle, null, (nuint)Marshal.SizeOf(lvLocalItem), VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
                    PInvoke.WriteProcessMemory(processHandle, lvMemItem, &lvLocalItem, (nuint)Marshal.SizeOf(lvLocalItem), null);
                    PInvoke.SendMessage((HWND)hListview, LVM_SETITEMSTATE, (WPARAM)(nuint)itemIndex, (LPARAM)(nint)lvMemItem);
                    PInvoke.SendMessage((HWND)hListview, LVM_ENSUREVISIBLE, (WPARAM)(nuint)itemIndex, default);
                }
                finally
                {
                    if (processHandle != default)
                        PInvoke.CloseHandle(processHandle);
                }

            }//end if
        }
    }
}
