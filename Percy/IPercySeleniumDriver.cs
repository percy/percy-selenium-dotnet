using System;
using OpenQA.Selenium;

namespace PercyIO.Selenium
{
  public interface IPercySeleniumDriver
  {
    ICapabilities GetCapabilities();
    System.Collections.Generic.IDictionary<string, object> GetSessionDetails();
    String sessionId();
    String GetHost();
  }
}
