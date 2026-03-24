using System;
using System.Collections.Generic;

namespace PercyIO.Selenium
{
  public class Cache<TKey, TValue>
  {
    private readonly Dictionary<TKey, CacheItem<TValue>> _cache = new Dictionary<TKey, CacheItem<TValue>>();
    private readonly TimeSpan _defaultTtl;

    public Cache() : this(TimeSpan.FromMinutes(5)) { }

    public Cache(TimeSpan defaultTtl)
    {
      _defaultTtl = defaultTtl;
    }

    public void Store(TKey key, TValue value)
    {
      _cache[key] = new CacheItem<TValue>(value, _defaultTtl);
    }

    public void Store(TKey key, TValue value, TimeSpan ttl)
    {
      _cache[key] = new CacheItem<TValue>(value, ttl);
    }

    public TValue Get(TKey key)
    {
      if (!_cache.ContainsKey(key)) return default(TValue);
      var cached = _cache[key];
      if (cached.IsExpired)
      {
        _cache.Remove(key);
        return default(TValue);
      }
      return cached.Value;
    }

    public void Remove(TKey key)
    {
      _cache.Remove(key);
    }

    public void Clear()
    {
      _cache.Clear();
    }
  }

  public class CacheItem<T>
  {
    private readonly DateTime _createdAt;
    private readonly TimeSpan _ttl;

    public CacheItem(T value, TimeSpan ttl)
    {
      Value = value;
      _createdAt = DateTime.UtcNow;
      _ttl = ttl;
    }

    public T Value { get; }
    public bool IsExpired => DateTime.UtcNow - _createdAt > _ttl;
  }
}
