namespace LineTrans.Core.Dictionary;

/// <summary>
/// 固定容量的线程安全 LRU 缓存，等价于 Kotlin 的
/// <c>LinkedHashMap&lt;K,V&gt;(64, 0.75f, true)</c> + <c>removeEldestEntry(size &gt; 200)</c>。
/// 使用 .NET 内置的 LinkedList + Dictionary 实现，命中后把节点移到链表尾部（最近使用）。
/// </summary>
/// <typeparam name="TValue">缓存值类型。</typeparam>
public sealed class LruCache<TValue>
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();

    private long _hits;
    private long _misses;
    private long _evictions;

    private sealed class Entry
    {
        public required string Key;
        public required TValue Value;
    }

    /// <summary>创建缓存。</summary>
    /// <param name="capacity">容量上限，必须大于 0。</param>
    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须大于 0");
        _capacity = capacity;
        _map = new Dictionary<string, LinkedListNode<Entry>>(capacity, StringComparer.Ordinal);
    }

    /// <summary>容量上限。</summary>
    public int Capacity => _capacity;

    /// <summary>当前条目数。</summary>
    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>命中次数（含重复命中）。</summary>
    public long Hits
    {
        get { lock (_gate) return _hits; }
    }

    /// <summary>未命中次数。</summary>
    public long Misses
    {
        get { lock (_gate) return _misses; }
    }

    /// <summary>因容量上限被淘汰的次数。</summary>
    public long Evictions
    {
        get { lock (_gate) return _evictions; }
    }

    /// <summary>尝试取值；命中时把该 key 标记为最近使用。</summary>
    public bool TryGet(string key, out TValue? value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                _hits++;
                value = node.Value.Value;
                return true;
            }

            _misses++;
            value = default;
            return false;
        }
    }

    /// <summary>写入并刷新 LRU 顺序；超出容量时淘汰最久未使用的条目。</summary>
    public void Set(string key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value.Value = value;
                _order.Remove(existing);
                _order.AddLast(existing);
                return;
            }

            var node = _order.AddLast(new Entry { Key = key, Value = value });
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _map.Remove(oldest.Value.Key);
                _evictions++;
            }
        }
    }

    /// <summary>清空全部条目与统计。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _hits = 0;
            _misses = 0;
            _evictions = 0;
        }
    }

    /// <summary>清空条目但保留统计计数。</summary>
    public void ClearEntries()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }

    /// <summary>导出当前 key 集合（调试 / 测试用）。</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            var list = new List<string>(_map.Count);
            foreach (var node in _order) list.Add(node.Key);
            return list;
        }
    }
}
