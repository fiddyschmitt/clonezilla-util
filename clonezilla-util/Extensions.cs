using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.Security.Cryptography;

namespace clonezilla_util
{
    public static class Extensions
    {
        public static bool IsEqualTo(this byte[] buffer1, byte[] otherBuffer)
        {
            var span1 = new ReadOnlySpan<byte>(buffer1, 0, buffer1.Length);
            var span2 = new ReadOnlySpan<byte>(otherBuffer, 0, otherBuffer.Length);
            var result = span1.SequenceEqual(span2);
            return result;
        }

        public static List<List<T>> ChunkBy<T>(this IEnumerable<T> source, int chunkSize)
        {
            return source
                .Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / chunkSize)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();
        }

        public static IEnumerable<ulong> Range(ulong start, ulong length)
        {
            var end = start + length;
            for (ulong i = start; i < end; i++)
            {
                yield return i;
            }
        }

        public static string ReplaceFirst(this string text, string search, string replace)
        {
            int pos = text.IndexOf(search);
            if (pos < 0)
            {
                return text;
            }
            return string.Concat(text.AsSpan(0, pos), replace, text.AsSpan(pos + search.Length));
        }
    }
}
