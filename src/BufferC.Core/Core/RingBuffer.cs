namespace BufferC.Core.Core;

/// <summary>线程安全环形缓冲（日志/事件/命令历史等监控数据）</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _buf;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    public RingBuffer(int capacity) => _buf = new T[capacity];

    public void Add(T item)
    {
        lock (_lock)
        {
            _buf[_head] = item;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    /// <summary>返回最近 n 条（从最旧到最新）</summary>
    public List<T> GetTail(int n)
    {
        lock (_lock)
        {
            var take = Math.Min(n, _count);
            var result = new List<T>(take);
            int start = (_head - _count + _buf.Length) % _buf.Length;
            for (int i = 0; i < take; i++)
                result.Add(_buf[(start + i) % _buf.Length]);
            return result;
        }
    }

    public int Count { get { lock (_lock) return _count; } }
}
