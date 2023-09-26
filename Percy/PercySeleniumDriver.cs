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
    PropertyInfo idProperty = typeof(WebElement).GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic);
    string elementId = (string)idProperty.GetValue(element);
    return elementId;
  } 

  public RemoteWebDriver getRemoteWebDriver()
  {
    return this._remoteDriver;
  }

  public ICapabilities GetCapabilities()
  {
    // Implement Cache
    var key = "caps_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      object capabilities = this._remoteDriver.Capabilities;
      PercyDriver.cache.Store(key, capabilities);
    }
    return (ICapabilities)PercyDriver.cache.Get(key);
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
      FieldInfo remoteServerUriField = typeof(HttpCommandExecutor).GetField("remoteServerUri", BindingFlags.NonPublic | BindingFlags.Instance);
      String commandExecutorUrl = remoteServerUriField.GetValue(executor).ToString();
      PercyDriver.cache.Store(key, commandExecutorUrl);
    }
    return (String)PercyDriver.cache.Get(key);
  }
 }
}
