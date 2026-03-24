using Xunit;
namespace PercyIO.Selenium.Tests
{
  public class CacheTest
  {
    private readonly CacheItem<object> cacheItem;
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
  }
}
