using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Reflection.Metadata.BlobBuilder;

namespace libCommon.Lists
{
    public class LazyList<T> : IList<T>
    {
        private readonly IEnumerator<T> _source;
        private readonly List<T> _internalList = [];
        private bool _isSourceExhausted = false;

        public bool FinishedReading
        {
            get => _isSourceExhausted;
        }

        public LazyList(IEnumerable<T> source)
        {
            _source = source.GetEnumerator();
        }

        public T this[int index]
        {
            get
            {
                EnsureExists(index);
                return _internalList[index];
            }
            set
            {
                EnsureExists(index);
                _internalList[index] = value;
            }
        }

        public int Count
        {
            get
            {
                EnsureAllLoaded();
                return _internalList.Count;
            }
        }

        public int CountSoFar => _internalList.Count;

        private bool EnsureExists(int desiredIndex)
        {
            while (_internalList.Count <= desiredIndex && !_isSourceExhausted)
            {
                if (_source.MoveNext())
                {
                    _internalList.Add(_source.Current);
                }
                else
                {
                    _isSourceExhausted = true;
                }

                Log.Debug($"Currently at index {_internalList.Count - 1:N0}, seeking toward desired index {desiredIndex:N0}");
            }


            if (desiredIndex >= _internalList.Count)
            {
                return false;
            }

            return true;
        }

        private void EnsureAllLoaded()
        {
            while (!_isSourceExhausted)
            {
                if (_source.MoveNext())
                {
                    _internalList.Add(_source.Current);
                }
                else
                {
                    _isSourceExhausted = true;
                }
            }
        }

        // Other IList<T> methods would need to be implemented here.

        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(T item) => _internalList.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _internalList.CopyTo(array, arrayIndex);
        public bool Remove(T item) => throw new NotSupportedException();
        public IEnumerator<T> GetEnumerator()
        {
            int i = 0;
            while (EnsureExists(i))
            {
                var block = this[i];
                yield return block;
                i++;

                Log.Debug($"GetEnumerator(): FinishedReading = {FinishedReading}, i = {i:N0}");
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int IndexOf(T item) => _internalList.IndexOf(item);
        public void Insert(int index, T item) => throw new NotSupportedException();
        public bool IsReadOnly => false;
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
