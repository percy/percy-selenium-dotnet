using System.Collections.Concurrent;

namespace PercyIO.Selenium
{
  public class Cache<TKey, TValue>
  {
    private readonly ConcurrentDictionary<TKey, CacheItem<TValue>> _cache = new ConcurrentDictionary<TKey, CacheItem<TValue>>();

    public void Store(TKey key, TValue value)
    {
      _cache[key] = new CacheItem<TValue>(value);
    }

    public TValue Get(TKey key)
    {
      if (!_cache.TryGetValue(key, out var cached)) return default(TValue);
      return cached.Value;
    }

    public void Remove(TKey key)
    {
      _cache.TryRemove(key, out _);
    }

    public void Clear()
    {
      _cache.Clear();
    }
  }

  public class CacheItem<T>
  {
    public CacheItem(T value)
    {
      Value = value;
    }
    public T Value { get; }
  }
}
