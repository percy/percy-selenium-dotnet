using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;

namespace PercyIO.Selenium
{
  internal interface IPercySeleniumDriver
  {
    ICapabilities GetCapabilities();
    System.Collections.Generic.IDictionary<string, object> GetSessionDetails();
    String sessionId();
    String GetElementIdFromElement(IWebElement element);
    String GetHost();
    RemoteWebDriver getRemoteWebDriver();
  }
}
