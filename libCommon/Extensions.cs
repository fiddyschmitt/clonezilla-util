using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using libCommon.Streams;
using libCommon.Streams.Sparse;

namespace libCommon
{
    public static class Extensions
    {
        public static string ToString(this IEnumerable<string> list, string seperator)
        {
            string result = string.Join(seperator, list);
            return result;
        }

        public static string ToPrettyFormat(this TimeSpan span)
        {
            if (span == TimeSpan.Zero) return "0 minutes";

            var sb = new StringBuilder();
            if (span.Days > 0)
                sb.AppendFormat("{0} day{1} ", span.Days, span.Days > 1 ? "s" : string.Empty);
            if (span.Hours > 0)
                sb.AppendFormat("{0} hour{1} ", span.Hours, span.Hours > 1 ? "s" : string.Empty);
            if (span.Minutes > 0)
                sb.AppendFormat("{0} minute{1} ", span.Minutes, span.Minutes > 1 ? "s" : string.Empty);
            return sb.ToString();
        }

        public static string EnsureEndsInPathSeparator(this string str)
        {
            string sepChar = Path.DirectorySeparatorChar.ToString();
            string altChar = Path.AltDirectorySeparatorChar.ToString();

            if (!str.EndsWith(sepChar) && !str.EndsWith(altChar))
            {
                str += sepChar;
            }
            return str;
        }

        public static bool IsEqualTo(this byte[] buffer1, byte[] otherBuffer)
        {
            var span1 = new ReadOnlySpan<byte>(buffer1, 0, buffer1.Length);
            var span2 = new ReadOnlySpan<byte>(otherBuffer, 0, otherBuffer.Length);
            var result = span1.SequenceEqual(span2);
            return result;
        }

        public static bool IsEqualTo(this byte[] buffer1, int start, int length, byte[] otherBuffer, int otherStart, int otherLength)
        {
            var span1 = new ReadOnlySpan<byte>(buffer1, start, length);
            var span2 = new ReadOnlySpan<byte>(otherBuffer, otherStart, otherLength);
            var result = span1.SequenceEqual(span2);
            return result;
        }

        public static string BytesToString(this int byteCount)
        {
            string result = BytesToString((long)byteCount);
            return result;
        }

        public static string BytesToString(this long byteCount)
        {
            string[] suf = { " B", " KB", " MB", " GB", " TB", " PB", " EB" }; //Longs run out around EB
            if (byteCount == 0)
                return "0" + suf[0];
            long bytes = Math.Abs(byteCount);
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return (Math.Sign(byteCount) * num).ToString() + suf[place];
        }

        public static string ToHexString(this byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public static long CopyTo(this Stream input, Stream output, int bufferSize, Action<long>? callBack = null)
        {
            byte[] buffer = Buffers.BufferPool.Rent(bufferSize);
            int read;
            long totalRead = 0;
            while ((read = input.Read(buffer, 0, bufferSize)) > 0)
            {
                totalRead += read;

                output.Write(buffer, 0, read);

                callBack?.Invoke(totalRead);
            }
            callBack?.Invoke(totalRead);

            Buffers.BufferPool.Return(buffer);

            return totalRead;
        }

        public static long CopyTo(this ISparseAwareReader input, ISparseAwareWriter output, int bufferSize, Action<long>? callBack)
        {
            byte[] buffer = Buffers.BufferPool.Rent(bufferSize);
            long totalRead = 0;
            while (true)
            {
                int read = input.Stream.Read(buffer, 0, bufferSize);

                if (read == 0)
                {
                    break;
                }

                totalRead += read;

                if (input.LatestReadWasAllNull && !output.ExplicitlyWriteNullBytes)
                {
                    //the input stream told us the entire read was null
                    //and the output stream doesn't care about writing null bytes (likely because it's writing to a sparse file)
                    output.Stream.Seek(read, SeekOrigin.Current);
                }
                else
                {
                    output.Stream.Write(buffer, 0, read);
                }

                callBack?.Invoke(totalRead);
            }

            callBack?.Invoke(output.Stream.Length);

            Buffers.BufferPool.Return(buffer);

            return totalRead;
        }

        //Forward seeking in Streams that don't support seeking
        public static void SkipTo(this Stream input, Stream output, long count, int bufferSize)
        {
            while (true)
            {
                var bytesRead = input.CopyTo(Stream.Null, bufferSize, bufferSize);

                if (bytesRead == 0) break;
            }
        }

        public static void ForEach<T>(this IEnumerable<T> enumeration, Action<T> action)
        {
            foreach (T item in enumeration)
            {
                action(item);
            }
        }

        public static long CopyTo(this Stream input, Stream output, long count, int bufferSize)
        {
            byte[] buffer = Buffers.BufferPool.Rent(bufferSize);
            long totalRead = 0;
            while (true)
            {
                if (count == 0) break;

                int read = input.Read(buffer, 0, (int)Math.Min(bufferSize, count));

                if (read == 0) break;
                totalRead += read;

                output.Write(buffer, 0, read);
                count -= read;
            }

            Buffers.BufferPool.Return(buffer);

            return totalRead;
        }

#pragma warning disable CS8601 // Possible null reference assignment.
#pragma warning disable CS8619 // Nullability of reference types in value doesn't match target type.
        public static IEnumerable<(T Previous, T Current, T Next)> Sandwich<T>(this IEnumerable<T> source, T beforeFirst = default, T afterLast = default)
        {
            var sourceList = source.ToList();

            T previous = beforeFirst;
            T current = sourceList.FirstOrDefault();

            foreach (var next in sourceList.Skip(1))
            {

                yield return (previous, current, next);

                previous = current;
                current = next;
            }

            yield return (previous, current, afterLast);
        }
#pragma warning restore CS8601 // Possible null reference assignment.
#pragma warning restore CS8619 // Nullability of reference types in value doesn't match target type.

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
    SafeFileHandle hDevice,
    int dwIoControlCode,
    IntPtr InBuffer,
    int nInBufferSize,
    IntPtr OutBuffer,
    int nOutBufferSize,
    ref int pBytesReturned,
    [In] ref NativeOverlapped lpOverlapped);

        public static void MarkAsSparse(this SafeFileHandle fileHandle)
        {
            int bytesReturned = 0;
            NativeOverlapped lpOverlapped = new NativeOverlapped();
            bool result =
                DeviceIoControl(
                    fileHandle,
                    590020, //FSCTL_SET_SPARSE,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    ref bytesReturned,
                    ref lpOverlapped);
            if (result == false)
                throw new Exception("Could not mark file as sparse.");
        }
    }
}
