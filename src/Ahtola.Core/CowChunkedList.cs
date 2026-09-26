using System.Collections;

namespace Ahtola.Core;

/// <summary>
/// A list stored as fixed-size chunks that can be shared copy-on-write with a clone.
/// </summary>
/// <remarks>
/// Every statement works on a clone of the catalog, so a table's row list is cloned once per
/// statement. With a flat list that clone (or the first write after it) copies the whole table,
/// which made every statement O(table rows) and a row-at-a-time bulk load O(rows²). Here a clone
/// copies only the chunk directory — one reference per <see cref="ChunkSize"/> elements — and the
/// first write to a chunk copies just that chunk.
/// <para>
/// Each chunk records the list that owns it. <see cref="ShareFrom"/> gives both lists fresh owner
/// tokens, so afterwards neither owns any chunk they share and each copies a chunk before its first
/// write to it. A list only ever writes into chunks it owns, so a shared chunk is never mutated.
/// </para>
/// </remarks>
internal sealed class CowChunkedList<T> : IList<T>, IReadOnlyList<T>
{
    private const int Shift = 10;
    internal const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private Chunk[] _chunks = [];
    private int _chunkCount;
    private int _count;
    private object _owner = new();
    private int _version;

    public int Count => _count;

    public bool IsReadOnly => false;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                ThrowIndexOutOfRange();
            return _chunks[index >> Shift].Items[index & Mask];
        }
        set
        {
            if ((uint)index >= (uint)_count)
                ThrowIndexOutOfRange();
            WritableChunk(index >> Shift)[index & Mask] = value;
            _version++;
        }
    }

    /// <summary>Makes this list an O(chunks) copy-on-write clone of <paramref name="source"/>.</summary>
    public void ShareFrom(CowChunkedList<T> source)
    {
        if (ReferenceEquals(source, this))
            return;

        _chunks = source._chunkCount == 0 ? [] : source._chunks[..source._chunkCount];
        _chunkCount = source._chunkCount;
        _count = source._count;
        _owner = new object();
        source._owner = new object();
        _version++;
    }

    public void Add(T item)
    {
        var offset = _count & Mask;
        var items = offset == 0 && _count == _chunkCount * ChunkSize
            ? AppendChunk()
            : WritableChunk(_count >> Shift);
        items[offset] = item;
        _count++;
        _version++;
    }

    public void AddRange(IEnumerable<T> items)
    {
        if (ReferenceEquals(items, this))
            items = this.ToArray();
        foreach (var item in items)
            Add(item);
    }

    public void Clear()
    {
        _chunks = [];
        _chunkCount = 0;
        _count = 0;
        _version++;
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public int IndexOf(T item)
    {
        var remaining = _count;
        for (var chunk = 0; chunk < _chunkCount && remaining > 0; chunk++)
        {
            var length = Math.Min(remaining, ChunkSize);
            var found = Array.IndexOf(_chunks[chunk].Items, item, 0, length);
            if (found >= 0)
                return (chunk << Shift) + found;
            remaining -= length;
        }

        return -1;
    }

    public int FindIndex(Predicate<T> match)
    {
        for (var index = 0; index < _count; index++)
        {
            if (match(_chunks[index >> Shift].Items[index & Mask]))
                return index;
        }

        return -1;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || array.Length - arrayIndex < _count)
            throw new ArgumentException("Destination array is not long enough.", nameof(array));

        var remaining = _count;
        for (var chunk = 0; chunk < _chunkCount && remaining > 0; chunk++)
        {
            var length = Math.Min(remaining, ChunkSize);
            Array.Copy(_chunks[chunk].Items, 0, array, arrayIndex, length);
            arrayIndex += length;
            remaining -= length;
        }
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_count)
            ThrowIndexOutOfRange();
        if (index == _count)
        {
            Add(item);
            return;
        }

        // Grow by one (duplicating the last element), then shift [index, count - 1) right by one.
        Add(this[_count - 1]);
        var last = _count - 1;
        var chunk = last >> Shift;
        var firstChunk = index >> Shift;
        while (chunk >= firstChunk)
        {
            var items = WritableChunk(chunk);
            var start = chunk == firstChunk ? index & Mask : 0;
            var end = chunk == last >> Shift ? last & Mask : Mask;
            if (end > start)
                Array.Copy(items, start, items, start + 1, end - start);
            if (chunk > firstChunk)
                items[0] = _chunks[chunk - 1].Items[Mask];
            chunk--;
        }

        WritableChunk(firstChunk)[index & Mask] = item;
        _version++;
    }

    public bool Remove(T item)
    {
        var index = IndexOf(item);
        if (index < 0)
            return false;

        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count)
            ThrowIndexOutOfRange();

        var last = _count - 1;
        var lastChunk = last >> Shift;
        for (var chunk = index >> Shift; chunk <= lastChunk; chunk++)
        {
            var items = WritableChunk(chunk);
            var start = chunk == index >> Shift ? index & Mask : 0;
            var end = chunk == lastChunk ? last & Mask : Mask;
            if (end > start)
                Array.Copy(items, start + 1, items, start, end - start);
            if (chunk < lastChunk)
                items[Mask] = _chunks[chunk + 1].Items[0];
        }

        // The vacated slot is in a chunk this list now owns, so clearing it cannot touch a clone.
        WritableChunk(lastChunk)[last & Mask] = default!;
        _count--;
        if ((_count & Mask) == 0 && _chunkCount > (_count >> Shift))
            _chunks[--_chunkCount] = null!;
        _version++;
    }

    public void Sort()
    {
        var items = this.ToArray();
        Array.Sort(items);
        for (var index = 0; index < items.Length; index++)
            WritableChunk(index >> Shift)[index & Mask] = items[index];
        _version++;
    }

    public List<T> GetRange(int index, int count)
    {
        if (index < 0 || count < 0 || _count - index < count)
            throw new ArgumentException("Index and count do not denote a valid range of elements.");

        var range = new List<T>(count);
        for (var offset = 0; offset < count; offset++)
            range.Add(this[index + offset]);
        return range;
    }

    public IEnumerator<T> GetEnumerator()
    {
        var version = _version;
        for (var index = 0; index < _count; index++)
        {
            if (version != _version)
                ThrowModified();
            yield return _chunks[index >> Shift].Items[index & Mask];
        }

        if (version != _version)
            ThrowModified();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private T[] WritableChunk(int chunk)
    {
        var current = _chunks[chunk];
        if (ReferenceEquals(current.Owner, _owner))
            return current.Items;

        var copy = new Chunk(_owner);
        Array.Copy(current.Items, copy.Items, ChunkSize);
        _chunks[chunk] = copy;
        return copy.Items;
    }

    private T[] AppendChunk()
    {
        if (_chunkCount == _chunks.Length)
            Array.Resize(ref _chunks, Math.Max(4, _chunks.Length * 2));

        var chunk = new Chunk(_owner);
        _chunks[_chunkCount++] = chunk;
        return chunk.Items;
    }

    private static void ThrowIndexOutOfRange()
        => throw new ArgumentOutOfRangeException("index", "Index was out of range.");

    private static void ThrowModified()
        => throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");

    private sealed class Chunk(object owner)
    {
        public readonly T[] Items = new T[ChunkSize];

        public readonly object Owner = owner;
    }
}
