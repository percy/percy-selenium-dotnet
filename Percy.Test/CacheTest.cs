using System;
using System.Threading;
using Xunit;
namespace PercyIO.Selenium.Tests
{
  public class CacheTest
  {
    Cache<string, object> _cache;

    public CacheTest()
    {
      _cache = new Cache<string, object>();
    }

    [Fact]
    public void Get_ShouldGetNullValue_WhenDoesNotExists()
    {
      // Arrange
      _cache.Clear();
      string expected = null;
      // Act
      string actual = (string)_cache.Get("abc");
      // Assert
      Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldRemoveKey_WhenExists()
    {
      // Arrange
      _cache.Clear();
      string expected = null;
      _cache.Store("A", "abc");
      // Act
      _cache.Remove("A");
      // Assert
      var actual = (string)_cache.Get("A");
      Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldStoreAndRetrieveValue()
    {
      _cache.Clear();
      _cache.Store("key1", "value1");
      Assert.Equal("value1", (string)_cache.Get("key1"));
    }

    [Fact]
    public void ShouldExpireAfterTtl()
    {
      var shortCache = new Cache<string, object>(TimeSpan.FromMilliseconds(50));
      shortCache.Store("expiring", "value");

      Assert.Equal("value", (string)shortCache.Get("expiring"));

      Thread.Sleep(100);

      Assert.Null(shortCache.Get("expiring"));
    }

    [Fact]
    public void ShouldSupportCustomTtlPerEntry()
    {
      _cache.Clear();
      _cache.Store("short", "shortval", TimeSpan.FromMilliseconds(50));
      _cache.Store("long", "longval", TimeSpan.FromMinutes(5));

      Thread.Sleep(100);

      Assert.Null(_cache.Get("short"));
      Assert.Equal("longval", (string)_cache.Get("long"));
    }

    [Fact]
    public void ShouldOverwriteExistingKey()
    {
      _cache.Clear();
      _cache.Store("key", "first");
      _cache.Store("key", "second");
      Assert.Equal("second", (string)_cache.Get("key"));
    }

    [Fact]
    public void ClearShouldRemoveAllEntries()
    {
      _cache.Store("a", "1");
      _cache.Store("b", "2");
      _cache.Clear();
      Assert.Null(_cache.Get("a"));
      Assert.Null(_cache.Get("b"));
    }
  }
}
