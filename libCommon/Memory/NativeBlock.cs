using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace libCommon.Memory
{
    /// <summary>
    /// One cache slot's decoded bytes, held OUTSIDE the GC heap. A 16-32 MB allocation is routed to
    /// VirtualAlloc by the CRT, so <see cref="Free"/> returns the pages to the OS immediately - which is
    /// exactly what lets <see cref="MemoryGovernor"/> steer by the machine's real available memory. It
    /// also keeps these multi-MB buffers off the LOH entirely (no fragmentation, no pool retention).
    ///
    /// LIFETIME IS LOAD-BEARING: a use-after-free here is an access violation, not merely wrong bytes. A
    /// block's pointer is dereferenced (<see cref="CopyTo"/>) and freed only under the owning
    /// CachingStream's lock, or by the finalizer once the whole stream is unreachable. See the
    /// lock-ordering rules at the top of CachingStream.
    /// </summary>
    public sealed class NativeBlock
    {
        nint ptr;
        readonly int length;
        readonly MemoryGovernor governor;

        /// <summary>Live block count / byte total across the process. Interlocked; asserted by tests to
        /// prove every allocation is matched by a free.</summary>
        public static long LiveBlocks;
        public static long LiveBytes;

        public int Length => length;

        NativeBlock(nint ptr, int length, MemoryGovernor governor)
        {
            this.ptr = ptr;
            this.length = length;
            this.governor = governor;
        }

        /// <summary>
        /// Allocates a block whose bytes have ALREADY been reserved with <see cref="MemoryGovernor.Admit"/>.
        /// The returned block owns that reservation and releases it in <see cref="Free"/>. On allocation
        /// failure this throws WITHOUT releasing - the caller still holds the reservation and releases it on
        /// its own error path.
        /// </summary>
        public static unsafe NativeBlock AllocateReserved(int length, MemoryGovernor governor)
        {
            // NativeMemory.Alloc throws OutOfMemoryException on failure (it never returns null).
            void* p = NativeMemory.Alloc((nuint)length);
            Interlocked.Increment(ref LiveBlocks);
            Interlocked.Add(ref LiveBytes, length);
            return new NativeBlock((nint)p, length, governor);
        }

        /// <summary>Copy <paramref name="count"/> bytes from a managed array into this block at offset 0.</summary>
        public void CopyFrom(byte[] src, int srcOffset, int count)
        {
            if (ptr == 0) throw new InvalidOperationException("NativeBlock used after free");
            if ((uint)count > (uint)length) throw new ArgumentOutOfRangeException(nameof(count));
            Marshal.Copy(src, srcOffset, ptr, count);
        }

        /// <summary>Copy <paramref name="count"/> bytes from this block (at <paramref name="srcOffset"/>)
        /// into a managed array. Callers MUST hold the owning stream's lock (see class remarks).</summary>
        public void CopyTo(long srcOffset, byte[] dst, int dstOffset, int count)
        {
            if (ptr == 0) throw new InvalidOperationException("NativeBlock used after free");
            if (srcOffset < 0 || count < 0 || srcOffset + count > length) throw new ArgumentOutOfRangeException(nameof(count));
            Marshal.Copy(ptr + (nint)srcOffset, dst, dstOffset, count);
        }

        /// <summary>Idempotent and thread-safe: frees the native pages and releases the governor reservation
        /// exactly once, whichever thread (or the finalizer) calls first.</summary>
        public unsafe void Free()
        {
            var p = Interlocked.Exchange(ref ptr, 0);
            if (p == 0) return;
            NativeMemory.Free((void*)p);
            Interlocked.Decrement(ref LiveBlocks);
            Interlocked.Add(ref LiveBytes, -length);
            governor.Release(length);
            GC.SuppressFinalize(this);
        }

        ~NativeBlock() => Free();
    }
}
