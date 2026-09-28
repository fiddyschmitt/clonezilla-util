using Microsoft.Win32.SafeHandles;
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
        public static extern int SevenZip_Open(in InStreamCallbacks callbacks, IntPtr ctx, string sevenZipDllPath, byte recursive, out ArchiveHandle handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_GetItemCount(ArchiveHandle handle, out uint count);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int SevenZip_GetItem(ArchiveHandle handle, uint index, out ItemInfo info,
            [Out] char[] pathBuf, uint pathBufChars, out uint pathChars);

        // On-demand seekable per-item stream (no extraction / no temp file)
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_OpenItemStream(ArchiveHandle handle, uint index, out ItemStreamHandle itemStream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_ItemRead(ItemStreamHandle itemStream, IntPtr buf, uint size, out uint processed);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SevenZip_ItemSeek(ItemStreamHandle itemStream, long offset, uint origin, out ulong newPosition);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SevenZip_ItemClose(IntPtr itemStream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SevenZip_Close(IntPtr handle);
    }

    // SafeHandles give the native handles a finalizer backstop: an archive or item stream that is never
    // disposed is still closed, instead of leaking its native state (gh review F37). Everything is disposed
    // explicitly today; this only matters for a future leak.

    /// <summary>A lib7zNative archive handle (SevenZip_Close).</summary>
    internal sealed class ArchiveHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ArchiveHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle()
        {
            SevenZipNativeInterop.SevenZip_Close(handle);
            return true;
        }
    }

    /// <summary>
    /// A lib7zNative item stream handle (SevenZip_ItemClose). It holds a reference on its archive's handle
    /// for its whole life, so SafeHandle's ref-counting defers the archive's close until this stream is
    /// closed, whatever order the two are disposed or finalized in. The order matters: closing the archive
    /// releases 7-Zip's codecs (CCodecs::CloseLibs), and an item stream closed after that would run on
    /// unloaded handler code. Finalizers alone guarantee no order.
    /// </summary>
    internal sealed class ItemStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        ArchiveHandle? archive;

        public ItemStreamHandle() : base(ownsHandle: true) { }

        internal void AttachTo(ArchiveHandle owner)
        {
            var added = false;
            owner.DangerousAddRef(ref added);
            if (added) archive = owner;
        }

        protected override bool ReleaseHandle()
        {
            SevenZipNativeInterop.SevenZip_ItemClose(handle);
            archive?.DangerousRelease();
            return true;
        }
    }
}
