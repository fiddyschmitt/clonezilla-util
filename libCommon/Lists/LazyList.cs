using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Lists
{
    public class LazyList<T> : IList<T>
    {
        private readonly IEnumerator<T> _source;
        private readonly List<T> _internalList = new();
        private bool _isSourceExhausted = false;

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

        private void EnsureExists(int index)
        {
            while (_internalList.Count <= index && !_isSourceExhausted)
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

            if (index >= _internalList.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");
            }
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
        public IEnumerator<T> GetEnumerator() => _internalList.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int IndexOf(T item) => _internalList.IndexOf(item);
        public void Insert(int index, T item) => throw new NotSupportedException();
        public bool IsReadOnly => false;
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
