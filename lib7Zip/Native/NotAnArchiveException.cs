using System;

namespace lib7Zip.Native
{
    /// <summary>
    /// Thrown when a stream contains no archive or filesystem that 7-Zip recognises (7-Zip returned
    /// S_FALSE from Open). This is expected for some partitions - e.g. a raw bios_grub / BIOS boot
    /// partition, swap, or unformatted space - and callers should treat it as "no browsable content"
    /// rather than an error.
    /// </summary>
    public sealed class NotAnArchiveException : Exception
    {
        public NotAnArchiveException() : base("The stream is not a recognised archive or filesystem.") { }
    }
}
