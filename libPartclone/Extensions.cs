using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone
{
    public static class Extensions
    {
        public static byte[] ToByteArray(this BitArray bits)
        {
            byte[] ret = new byte[(bits.Length - 1) / 8 + 1];
            bits.CopyTo(ret, 0);
            return ret;
        }

        public static string ToHexString(this IEnumerable<byte> ba)
        {
            return BitConverter.ToString(ba.ToArray()).Replace("-", "");
        }

        public static string ToString(this IEnumerable<string> list, string linePrefix, string separator)
        {
            string result = string.Join(separator, list.Select(line => $"{linePrefix}{line}"));
            return result;
        }

        public static string BytesToString(this ulong byteCount)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
            if (byteCount == 0)
                return "0" + suf[0];
            ulong bytes = byteCount;
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return num.ToString() + suf[place];
        }

        public static string[] ToLines(this string str)
        {
            var result = new List<string>();

            using (var sr = new StringReader(str))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    result.Add(line);
                }
            }

            return result.ToArray();
        }

        public static List<List<T>> ChunkBy<T>(this List<T> source, int chunkSize)
        {
            return source
                .Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / chunkSize)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();
        }

        public static IEnumerable<bool> GetBits(this byte b)
        {
            for (int i = 0; i < 8; i++)
            {
                yield return (b & 0x80) != 0;
                b *= 2;
            }
        }

        public static IEnumerable<IEnumerable<T>> GroupAdjacentBy<T>(
        this IEnumerable<T> source, Func<T, T, bool> predicate)
        {
            using var e = source.GetEnumerator();
            if (e.MoveNext())
            {
                var list = new List<T> { e.Current };
                var pred = e.Current;
                while (e.MoveNext())
                {
                    if (predicate(pred, e.Current))
                    {
                        list.Add(e.Current);
                    }
                    else
                    {
                        yield return list;
                        list = new List<T> { e.Current };
                    }
                    pred = e.Current;
                }
                yield return list;
            }
        }

        public static IEnumerable<T> Cached<T>(this IEnumerable<T> enumerable)
        {
            return CachedImpl(enumerable.GetEnumerator(), new List<T>());
        }

        static IEnumerable<T> CachedImpl<T>(IEnumerator<T> source, List<T> buffer)
        {
            int pos = 0;
            while (true)
            {
                if (pos == buffer.Count)
                    if (source.MoveNext())
                        buffer.Add(source.Current);
                    else
                        yield break;
                yield return buffer[pos++];
            }
        }
    }

}
