using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using System.Reflection;
using System.Collections.Generic;

namespace PercyIO.Selenium
{
 internal class PercySeleniumDriver : IPercySeleniumDriver
 {
  private WebDriver _driver;

  internal PercySeleniumDriver(WebDriver driver)
  {
    if(driver == null) throw new ArgumentNullException(nameof(driver));
    this._driver = driver;
  }

  public String GetElementIdFromElement(IWebElement element)
  {
    PropertyInfo idProperty = typeof(WebElement).GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic);
    string elementId = (string)idProperty.GetValue(element);
    return elementId;
  } 

  public WebDriver getWebDriver()
  {
    return this._driver;
  }

  public Dictionary<string, object> GetCapabilities()
  {
    var key = "caps_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      var hasCapabilities = this._driver as IHasCapabilities;
      if (hasCapabilities == null) return null;
      object capabilities = hasCapabilities.Capabilities;
      var capabilitiesType = capabilities.GetType();
      var dictionaryField = capabilitiesType.GetField("capabilities", BindingFlags.NonPublic | BindingFlags.Instance);

      if (dictionaryField != null){
        var capabilitiesDictionary = dictionaryField.GetValue(capabilities) as Dictionary<string, object>;
        if (capabilitiesDictionary != null){
          PercyDriver.cache.Store(key, capabilitiesDictionary);
        }
      }
    }

    return PercyDriver.cache.Get(key) as Dictionary<string, object>;
  }

  public IDictionary<string, object> GetSessionDetails()
  {
    var key = "session_caps_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      var hasCapabilities = this._driver as IHasCapabilities;
      if (hasCapabilities == null) return null;
      object session_caps = hasCapabilities.Capabilities;
      PercyDriver.cache.Store(key, session_caps);
    }
    return (IDictionary<string, object>)PercyDriver.cache.Get(key);
  }

  public String sessionId()
  {
    var hasSessionId = this._driver as IHasSessionId;
    return hasSessionId?.SessionId?.ToString()!;
  }

  public String GetHost()
  {
    var key = "command_executor_" + sessionId();
    if (PercyDriver.cache.Get(key) == null) {
      var commandExecutor = this._driver.CommandExecutor;
      if (commandExecutor is HttpCommandExecutor httpExecutor)
      {
        FieldInfo remoteServerUriField = typeof(HttpCommandExecutor).GetField("remoteServerUri", BindingFlags.NonPublic | BindingFlags.Instance);
        String commandExecutorUrl = remoteServerUriField.GetValue(httpExecutor).ToString();
        PercyDriver.cache.Store(key, commandExecutorUrl);
      }
    }
    return (String)PercyDriver.cache.Get(key);
  }
 }
}
