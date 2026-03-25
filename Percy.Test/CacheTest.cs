using System.Threading;
using System.Threading.Tasks;
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
      string? expected = null;
      // Act
      string? actual = (string?)_cache.Get("abc");
      // Assert
      Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldRemoveKey_WhenExists()
    {
      // Arrange
      _cache.Clear();
      string? expected = null;
      _cache.Store("A", "abc");
      // Act
      _cache.Remove("A");
      // Assert
      var actual = (string?)_cache.Get("A");
      Assert.Equal(expected, actual);
    }

    [Fact]
    public void ShouldStoreAndRetrieveValue()
    {
      _cache.Clear();
      _cache.Store("key1", "value1");
      var actual = (string?)_cache.Get("key1");
      Assert.Equal("value1", actual);
    }

    [Fact]
    public void ShouldOverwriteExistingValue()
    {
      _cache.Clear();
      _cache.Store("key1", "value1");
      _cache.Store("key1", "value2");
      var actual = (string?)_cache.Get("key1");
      Assert.Equal("value2", actual);
    }

    [Fact]
    public void ShouldClearAllEntries()
    {
      _cache.Clear();
      _cache.Store("a", "1");
      _cache.Store("b", "2");
      _cache.Clear();
      Assert.Null(_cache.Get("a"));
      Assert.Null(_cache.Get("b"));
    }

    [Fact]
    public void ShouldBeThreadSafe_ConcurrentStoreAndGet()
    {
      _cache.Clear();
      int iterations = 100;
      var tasks = new Task[iterations];

      for (int i = 0; i < iterations; i++)
      {
        int index = i;
        tasks[i] = Task.Run(() =>
        {
          _cache.Store($"key_{index}", $"value_{index}");
          _cache.Get($"key_{index}");
        });
      }

      Task.WaitAll(tasks);

      // Verify no exceptions were thrown and at least some values are stored
      for (int i = 0; i < iterations; i++)
      {
        var val = _cache.Get($"key_{i}");
        Assert.Equal($"value_{i}", val);
      }
    }

    [Fact]
    public void ShouldBeThreadSafe_ConcurrentRemove()
    {
      _cache.Clear();
      for (int i = 0; i < 50; i++)
        _cache.Store($"key_{i}", $"value_{i}");

      var tasks = new Task[50];
      for (int i = 0; i < 50; i++)
      {
        int index = i;
        tasks[i] = Task.Run(() => _cache.Remove($"key_{index}"));
      }

      Task.WaitAll(tasks);

      for (int i = 0; i < 50; i++)
        Assert.Null(_cache.Get($"key_{i}"));
    }
  }
}
