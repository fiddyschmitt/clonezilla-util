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
            List<T> queue = new(source);

            while (queue.Count > 0)
            {
                var item = queue[0];
                queue.RemoveAt(0);

                var children = childSelector(item);

                if (depthFirst)
                {
                    queue.InsertRange(0, children);
                }
                else
                {
                    queue.AddRange(children);
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
                    return new List<T>();
                }
                else
                {
                    return new List<T>() { child };
                }
            });

            foreach (var result in Recurse(list, childListSelector, depthFirst))
            {
                yield return result;
            }
        }

        //This achieves 3 things:
        //1. Processes in parallel
        //2. Preserves original order
        //3. Returns items immediately after they are available
        public static IEnumerable<TOutput> SelectParallelPreserveOrder<TInput, TOutput>(this IEnumerable<TInput> list, Func<TInput, TOutput> selector, int? threads = null)
        {
            var outputItems = new BlockingCollection<int>();
            var resultDictionary = new ConcurrentDictionary<int, TOutput>();

            threads ??= Environment.ProcessorCount;

            Task.Factory.StartNew(() =>
            {
                list
                    .Select((item, index) => new
                    {
                        Index = index,
                        Value = item
                    })
                    .AsParallel()
                    .WithDegreeOfParallelism(threads.Value)
                    .ForAll(item =>
                    {
                        var result = selector(item.Value);
                        resultDictionary.TryAdd(item.Index, result);
                        outputItems.Add(item.Index);
                    });

                outputItems.CompleteAdding();
            });

            var currentIndexToReturn = 0;
            foreach (var finishedIndex in outputItems.GetConsumingEnumerable())
            {
                while (resultDictionary.TryGetValue(currentIndexToReturn, out var result))
                {
                    yield return result;

                    resultDictionary.TryRemove(currentIndexToReturn, out var _);
                    currentIndexToReturn++;
                };
            }
        }

        public static IEnumerable<T> Buffer<T>(this IEnumerable<T> input, Action<(int InputDiscovered, bool InputFinished, int OutputProcessed)>? progressCallback = null)
        {
            int totalInputDiscovered = 0;
            int totalOutputProcessed = 0;
            bool inputFinished = false;

            var blockingCollection = new BlockingCollection<T>();

            //read from the input
            Task.Factory.StartNew(() =>
            {
                foreach (var item in input)
                {
                    blockingCollection.Add(item);
                    totalInputDiscovered++;
                }

                progressCallback?.Invoke((totalInputDiscovered, inputFinished, totalOutputProcessed));

                blockingCollection.CompleteAdding();
                inputFinished = true;
            });

            foreach (var item in blockingCollection.GetConsumingEnumerable())
            {
                yield return item;

                totalOutputProcessed++;
                progressCallback?.Invoke((totalInputDiscovered, inputFinished, totalOutputProcessed));
            }
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

        public static string BytesToString(this uint bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this int bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this long bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this ulong bytes)
        {
            string[] UNITS = new string[] { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            int c;
            for (c = 0; c < UNITS.Length; c++)
            {
                ulong m = (ulong)1 << ((c + 1) * 10);
                if (bytes < m)
                    break;
            }

            double n = bytes / (double)((ulong)1 << (c * 10));
            return string.Format("{0:0.##} {1}", n, UNITS[c]);
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

        public static long Sparsify(this ISparseAwareReader input, ISparseAwareWriter output, int bufferSize, Action<long>? callBack)
        {
            output.Stream.SetLength(input.Stream.Length);

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

        public static IEnumerable<(T? Previous, T Current, T? Next)> Sandwich<T>(this IEnumerable<T> source, T? beforeFirst = default, T? afterLast = default)
        {
            var sourceList = source.ToList();

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

        [SupportedOSPlatform("windows")]
        public static void MarkAsSparse(this SafeFileHandle fileHandle)
        {
            int bytesReturned = 0;
            NativeOverlapped lpOverlapped = new();
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

        public static TRange? BinarySearch<TRange, TValue>(this IList<TRange> ranges,
                                                       TValue value,
                                                       IRangeComparer<TRange, TValue> comparer)
        {
            int min = 0;
            int max = ranges.Count - 1;

            while (min <= max)
            {
                int mid = (min + max) / 2;
                int comparison = comparer.Compare(ranges[mid], value);
                if (comparison == 0)
                {
                    return ranges[mid];
                }
                if (comparison < 0)
                {
                    min = mid + 1;
                }
                else if (comparison > 0)
                {
                    max = mid - 1;
                }
            }

            var result = default(TRange);
            return result;
        }
    }
}
