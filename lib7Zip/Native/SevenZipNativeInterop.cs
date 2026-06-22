using System;
using System.Runtime.InteropServices;

namespace lib7Zip.Native
{
    /// <summary>
    /// Raw P/Invoke surface for lib7zNative.dll (see lib7zNative/include/lib7znative.h).
    /// All functions return an HRESULT (0 == S_OK). On x64 the calling convention is unified,
    /// so Cdecl here matches the plain C exports.
    /// </summary>
    internal static class SevenZipNativeInterop
    {
        const string Dll = "lib7zNative";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ReadFn(IntPtr ctx, IntPtr buf, uint size, out uint processed);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SeekFn(IntPtr ctx, long offset, uint origin, out ulong newPosition);

        [StructLayout(LayoutKind.Sequential)]
        public struct InStreamCallbacks
        {
            public ReadFn Read;
            public SeekFn Seek;
        }

        // Mirrors SevenZipItemInfo. Natural alignment matches the C struct (2 bytes + 6 pad + 6x 8-byte fields).
        [StructLayout(LayoutKind.Sequential)]
        public struct ItemInfo
        {
            public byte IsDir;
            public byte HasOffset;
            public ulong Size;
            public long Offset;
            public long ModifiedFileTime;
            public long CreatedFileTime;
            public long AccessedFileTime;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int SevenZip_Open(in InStreamCallbacks callbacks, IntPtr ctx, string sevenZipDllPath, byte recursive, out IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_GetItemCount(IntPtr handle, out uint count);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int SevenZip_GetItem(IntPtr handle, uint index, out ItemInfo info,
            [Out] char[] pathBuf, uint pathBufChars, out uint pathChars);

        // On-demand seekable per-item stream (no extraction / no temp file)
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_OpenItemStream(IntPtr handle, uint index, out IntPtr itemStream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_ItemRead(IntPtr itemStream, IntPtr buf, uint size, out uint processed);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_ItemSeek(IntPtr itemStream, long offset, uint origin, out ulong newPosition);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SevenZip_ItemClose(IntPtr itemStream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SevenZip_Close(IntPtr handle);
    }
}
