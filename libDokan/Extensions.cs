using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan
{
    public static class Extensions
    {
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
    }
}
