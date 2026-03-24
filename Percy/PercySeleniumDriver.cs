using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using System.Reflection;
using System.Collections.Generic;

namespace PercyIO.Selenium
{
 internal class PercySeleniumDriver : IPercySeleniumDriver
 {
  private object driver;
  private RemoteWebDriver _remoteDriver;

  internal PercySeleniumDriver(RemoteWebDriver driver)
  {
    if(!isDriverValid(driver)) throw new Exception("Driver should be of type RemoteWebDriver");
    this.driver = driver;
    this._remoteDriver = driver;
  }

  private bool isDriverValid(WebDriver driver)
  {
      return (bool) (
          driver is RemoteWebDriver &&
          driver.GetType().ToString().Contains("Selenium")
      );
  }

  public String GetElementIdFromElement(IWebElement element)
  {
    if (element == null)
      throw new ArgumentNullException(nameof(element), "WebElement must not be null");

    PropertyInfo? idProperty = typeof(WebElement).GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic);
    if (idProperty == null)
      throw new InvalidOperationException(
        "Could not find internal 'Id' property on WebElement. " +
        "This may indicate an incompatible Selenium WebDriver version.");

    object? value = idProperty.GetValue(element);
    if (value == null)
      throw new InvalidOperationException("WebElement Id property returned null");

    return value.ToString()!;
  }

  public RemoteWebDriver getRemoteWebDriver()
  {
    return this._remoteDriver;
  }

  public Dictionary<string, object> GetCapabilities()
  {
    var key = "caps_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      object capabilities = this._remoteDriver.Capabilities;
      var capabilitiesType = capabilities.GetType();
      var dictionaryField = capabilitiesType.GetField("capabilities", BindingFlags.NonPublic | BindingFlags.Instance);

      if (dictionaryField != null){
        var capabilitiesDictionary = dictionaryField.GetValue(capabilities) as Dictionary<string, object>;
        if (capabilitiesDictionary != null){
          PercyDriver.cache.Store(key, capabilitiesDictionary);
        }
      }

      // If reflection failed, fall back to extracting capabilities via the public API
      if (PercyDriver.cache.Get(key) == null) {
        var fallbackCaps = new Dictionary<string, object>();
        if (this._remoteDriver.Capabilities is ICapabilities caps) {
          // Extract known capability keys via the public interface
          foreach (var capName in new[] { "browserName", "browserVersion", "platformName", "osVersion" }) {
            var val = caps.GetCapability(capName);
            if (val != null)
              fallbackCaps[capName] = val;
          }
        }
        if (fallbackCaps.Count > 0)
          PercyDriver.cache.Store(key, fallbackCaps);
      }
    }

    return PercyDriver.cache.Get(key) as Dictionary<string, object>
      ?? new Dictionary<string, object>();
  }

  public IDictionary<string, object> GetSessionDetails()
  {
    var key = "session_caps_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      object session_caps  = this._remoteDriver.Capabilities;
      PercyDriver.cache.Store(key, session_caps);
    }
    return (IDictionary<string, object>)PercyDriver.cache.Get(key);
  }

  public String sessionId()
  {
    return this._remoteDriver?.SessionId?.ToString()!;
  }

  public String GetHost()
  {
    var key = "command_executor_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      HttpCommandExecutor executor = (HttpCommandExecutor)(this._remoteDriver).CommandExecutor;
      FieldInfo? remoteServerUriField = typeof(HttpCommandExecutor).GetField("remoteServerUri", BindingFlags.NonPublic | BindingFlags.Instance);

      if (remoteServerUriField == null)
        throw new InvalidOperationException(
          "Could not find internal 'remoteServerUri' field on HttpCommandExecutor. " +
          "This may indicate an incompatible Selenium WebDriver version.");

      object? uriValue = remoteServerUriField.GetValue(executor);
      if (uriValue == null)
        throw new InvalidOperationException("HttpCommandExecutor remoteServerUri is null");

      String commandExecutorUrl = uriValue.ToString()!;
      PercyDriver.cache.Store(key, commandExecutorUrl);
    }
    return (String)PercyDriver.cache.Get(key);
  }
 }
}
