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
    this.driver = driver;
    this._remoteDriver = driver;
  }

  public ICapabilities GetCapabilities()
  {
    // Implement Cache
    return this._remoteDriver?.Capabilities;
  }

  public IDictionary<string, object> GetSessionDetails()
  {
    // Implement Cache
    return (IDictionary<string, object>)this._remoteDriver?.Capabilities;
  }

  public String sessionId()
  {
    return this._remoteDriver?.SessionId?.ToString()!;
  }

  public String GetHost()
  {
    HttpCommandExecutor executor = (HttpCommandExecutor)(this._remoteDriver)?.CommandExecutor;
    FieldInfo remoteServerUriField = typeof(HttpCommandExecutor)?.GetField("remoteServerUri", BindingFlags.NonPublic | BindingFlags.Instance);
    String commandExecutorUrl = remoteServerUriField?.GetValue(executor)?.ToString();
    return commandExecutorUrl;
  }
 }
}
