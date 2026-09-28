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
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using libCommon.Lists;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace libCommon
{
    public static class Extensions
    {
        public static string ToString(this IEnumerable<string> list, string seperator)
        {
            string result = string.Join(seperator, list);
            return result;
        }

        public static IEnumerable<T> Recurse<T>(this IEnumerable<T> source, Func<T, IEnumerable<T>> childSelector, bool depthFirst = false)
        {
            //LinkedList gives O(1) dequeue-from-front and O(1) insertion at either end, so the
            //whole traversal is O(n). A List used as a queue was O(n^2): RemoveAt(0) (and the
            //depth-first InsertRange(0, ...)) shift every remaining element on each step.
            var queue = new LinkedList<T>(source);

            while (queue.Count > 0)
            {
                var item = queue.First!.Value;
                queue.RemoveFirst();

                var children = childSelector(item);

                if (depthFirst)
                {
                    //insert children at the front preserving their order (matches List.InsertRange(0, children))
                    LinkedListNode<T>? insertAfter = null;
                    foreach (var child in children)
                    {
                        insertAfter = insertAfter == null
                            ? queue.AddFirst(child)
                            : queue.AddAfter(insertAfter, child);
                    }
                }
                else
                {
                    foreach (var child in children)
                    {
                        queue.AddLast(child);
                    }
                }

                yield return item;
            }
        }

        public static IEnumerable<T> Recurse<T>(this T source, Func<T, T?> childSelector, bool depthFirst = false)
        {
            var list = new List<T>() { source };
            var childListSelector = new Func<T, IEnumerable<T>>(item =>
            {
                var child = childSelector(item);
                if (child == null)
                {
                    return [];
                }
                else
                {
                    return [child];
                }
            });

            foreach (var result in Recurse(list, childListSelector, depthFirst))
            {
                yield return result;
            }
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

        public static string BytesToString(this uint bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this int bytes) => BytesToString((long)bytes);

        //A negative length used to be cast straight to ulong, which made it an enormous positive number (gh review F18)
        public static string BytesToString(this long bytes) =>
            bytes < 0
                ? "-" + BytesToString((ulong)(-(bytes + 1)) + 1)   //-(bytes + 1) cannot overflow, even for long.MinValue
                : BytesToString((ulong)bytes);

        static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

        public static string BytesToString(this ulong bytes)
        {
            //The largest unit not bigger than the value. EB is the last unit (a ulong tops out at 16 EB), so the index
            //stays in the array and no shift reaches 64 bits: at >= 1 EB the old loop shifted by 70 (which wraps to 6)
            //and then indexed past the end (gh review F18).
            var c = 0;
            while (c < ByteUnits.Length - 1 && bytes >= 1UL << ((c + 1) * 10)) c++;

            double n = bytes / (double)(1UL << (c * 10));
            return string.Format("{0:0.##} {1}", n, ByteUnits[c]);
        }

        public static string ToHexString(this byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public static long CopyTo(this Stream input, Stream output, int bufferSize, Action<(long Read, long TotalRead)>? callBack = null)
        {
            var buffer = Buffers.BufferPool.Rent(bufferSize);
            try   //return the pooled buffer even when a read or write throws (gh review F20)
            {
                int read;
                var totalRead = 0L;
                while ((read = input.ReadAtLeast(buffer, bufferSize, false)) > 0)
                {
                    totalRead += read;

                    output.Write(buffer, 0, read);

                    callBack?.Invoke((read, totalRead));
                }
                callBack?.Invoke((0, totalRead));

                return totalRead;
            }
            finally
            {
                Buffers.BufferPool.Return(buffer);
            }
        }

        public static long Sparsify(this ISparseAwareReader input, ISparseAwareWriter output, int bufferSize, Action<(long Read, long TotalRead)>? callBack)
        {
            output.Stream.SetLength(input.Stream.Length);

            byte[] buffer = Buffers.BufferPool.Rent(bufferSize);
            try   //return the pooled buffer even when a read or write throws (gh review F20)
            {
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

                    callBack?.Invoke((read, totalRead));
                }

                callBack?.Invoke((0, output.Stream.Length));

                return totalRead;
            }
            finally
            {
                Buffers.BufferPool.Return(buffer);
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
            var buffer = Buffers.BufferPool.Rent(bufferSize);
            try   //return the pooled buffer even when a read or write throws (gh review F20)
            {
                var totalRead = 0L;

                //This has to be in a while loop, because the count that was requested coule be larger than Array.MaxLength
                while (true)
                {
                    if (count == 0) break;

                    var read = input.Read(buffer, 0, (int)Math.Min(bufferSize, count));

                    if (read == 0) break;
                    totalRead += read;

                    output.Write(buffer, 0, read);
                    count -= read;
                }

                return totalRead;
            }
            finally
            {
                Buffers.BufferPool.Return(buffer);
            }
        }

        public static IEnumerable<(T? Previous, T Current, T? Next)> Sandwich<T>(this IEnumerable<T> source, T? beforeFirst = default, T? afterLast = default)
        {
            var sourceList = source.ToList();

            if (sourceList.Count == 0) yield break;

            T? previous = beforeFirst;

            T current = sourceList.First();

            foreach (var next in sourceList.Skip(1))
            {
                yield return (previous, current, next);

                previous = current;
                current = next;
            }

            yield return (previous, current, afterLast);
        }

        [SupportedOSPlatform("windows")]
        public static unsafe void MarkAsSparse(this SafeFileHandle fileHandle)
        {
            uint bytesReturned = 0;
            NativeOverlapped lpOverlapped = new();
            bool result =
                PInvoke.DeviceIoControl(
                    (HANDLE)fileHandle.DangerousGetHandle(),
                    590020, //FSCTL_SET_SPARSE,
                    null,
                    0,
                    null,
                    0,
                    &bytesReturned,
                    &lpOverlapped);
            var error = Marshal.GetLastPInvokeError();
            GC.KeepAlive(fileHandle);
            if (result == false)
                throw new IOException("Could not mark the output file as sparse.", new System.ComponentModel.Win32Exception(error));   //with the reason (gh review F41)
        }
    }
}
