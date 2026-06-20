using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using static lib7Zip.Native.SevenZipNativeInterop;

namespace lib7Zip.Native
{
    public sealed class NativeArchiveEntry
    {
        public uint Index;
        public string Path = "";
        public bool IsDir;
        public long Size;
        public DateTime? Modified;
        public DateTime? Created;
        public DateTime? Accessed;
    }

    /// <summary>
    /// Managed wrapper over lib7zNative: opens a seekable .NET stream as an archive (format
    /// auto-detected), enumerates entries, and extracts a single entry to an output stream.
    /// NOT thread-safe - one instance == one open archive == one underlying stream; pool instances
    /// for concurrency (7-Zip's IInArchive is not reentrant).
    /// </summary>
    public sealed class SevenZipNativeArchive : IDisposable
    {
        readonly Stream stream;
        readonly bool ownsStream;
        IntPtr handle;

        // These delegates are handed to native code as function pointers and must stay alive
        // for as long as the native archive handle exists.
        readonly ReadFn readFn;
        readonly SeekFn seekFn;
        readonly byte[] readScratch = new byte[1 << 20];

        public SevenZipNativeArchive(Stream seekableStream, string sevenZipDllPath, bool ownsStream = true)
        {
            if (!seekableStream.CanSeek) throw new ArgumentException("Stream must be seekable", nameof(seekableStream));
            stream = seekableStream;
            this.ownsStream = ownsStream;
            readFn = Read;
            seekFn = Seek;

            var cb = new InStreamCallbacks { Read = readFn, Seek = seekFn };
            int hr = SevenZip_Open(in cb, IntPtr.Zero, sevenZipDllPath, out handle);
            if (hr != 0 || handle == IntPtr.Zero)
                throw new IOException($"lib7zNative SevenZip_Open failed: 0x{hr:X8}");
        }

        int Read(IntPtr ctx, IntPtr buf, uint size, out uint processed)
        {
            processed = 0;
            try
            {
                if (size == 0) return 0;
                int toRead = (int)Math.Min(size, (uint)readScratch.Length);
                int n = stream.Read(readScratch, 0, toRead);
                if (n > 0) Marshal.Copy(readScratch, 0, buf, n);
                processed = (uint)n;
                return 0;
            }
            catch { return unchecked((int)0x80004005); } // E_FAIL
        }

        int Seek(IntPtr ctx, long offset, uint origin, out ulong newPosition)
        {
            newPosition = 0;
            try
            {
                var so = origin == 0 ? SeekOrigin.Begin : origin == 1 ? SeekOrigin.Current : SeekOrigin.End;
                newPosition = (ulong)stream.Seek(offset, so);
                return 0;
            }
            catch { return unchecked((int)0x80004005); }
        }

        public IReadOnlyList<NativeArchiveEntry> GetEntries()
        {
            int hr = SevenZip_GetItemCount(handle, out uint count);
            if (hr != 0) throw new IOException($"lib7zNative GetItemCount failed: 0x{hr:X8}");

            var result = new List<NativeArchiveEntry>((int)count);
            var pathBuf = new char[512];
            for (uint i = 0; i < count; i++)
            {
                hr = SevenZip_GetItem(handle, i, out ItemInfo info, pathBuf, (uint)pathBuf.Length, out uint pathChars);
                if (hr != 0) throw new IOException($"lib7zNative GetItem({i}) failed: 0x{hr:X8}");
                if (pathChars >= pathBuf.Length)
                {
                    pathBuf = new char[pathChars + 1];
                    hr = SevenZip_GetItem(handle, i, out info, pathBuf, (uint)pathBuf.Length, out pathChars);
                    if (hr != 0) throw new IOException($"lib7zNative GetItem({i}) failed: 0x{hr:X8}");
                }
                result.Add(new NativeArchiveEntry
                {
                    Index = i,
                    Path = new string(pathBuf, 0, (int)pathChars),
                    IsDir = info.IsDir != 0,
                    Size = (long)info.Size,
                    Modified = FileTimeOrNull(info.ModifiedFileTime),
                    Created = FileTimeOrNull(info.CreatedFileTime),
                    Accessed = FileTimeOrNull(info.AccessedFileTime),
                });
            }
            return result;
        }

        public void ExtractTo(uint index, Stream output)
        {
            var scratch = new byte[1 << 20];
            WriteFn write = (IntPtr ctx, IntPtr buf, uint size, out uint processed) =>
            {
                processed = 0;
                try
                {
                    uint remaining = size;
                    long src = buf.ToInt64();
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(remaining, (uint)scratch.Length);
                        Marshal.Copy(new IntPtr(src), scratch, 0, chunk);
                        output.Write(scratch, 0, chunk);
                        src += chunk;
                        remaining -= (uint)chunk;
                    }
                    processed = size;
                    return 0;
                }
                catch { return unchecked((int)0x80004005); }
            };
            int hr = SevenZip_ExtractItem(handle, index, write, IntPtr.Zero);
            GC.KeepAlive(write);
            if (hr != 0) throw new IOException($"lib7zNative ExtractItem({index}) failed: 0x{hr:X8}");
        }

        static DateTime? FileTimeOrNull(long ft)
        {
            if (ft == 0) return null;
            try { return DateTime.FromFileTimeUtc(ft); } catch { return null; }
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                SevenZip_Close(handle);
                handle = IntPtr.Zero;
            }
            if (ownsStream) stream.Dispose();
        }
    }
}
