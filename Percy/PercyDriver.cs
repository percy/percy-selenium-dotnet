using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using System.Collections.Generic;

namespace PercyIO.Selenium
{
  public class PercyDriver
  {
    private IPercySeleniumDriver percySeleniumDriver;
    private String sessionId;
    internal void setValues(IPercySeleniumDriver percySeleniumDriver)
    {
      this.sessionId = percySeleniumDriver.sessionId();
    }

    internal Dictionary<string, object>  getPayload()
    {
      Dictionary<string, object>  payload = new Dictionary<string, object>()  
      {
        { "sessionId", this.sessionId },
        { "commandExecutorUrl", this.percySeleniumDriver.GetHost() },
        { "capabilities", this.percySeleniumDriver.GetCapabilities() },
      };
      return payload;
    }

    public PercyDriver(RemoteWebDriver driver)
    {
      this.percySeleniumDriver = new PercySeleniumDriver(driver);
      setValues(this.percySeleniumDriver);
    }

    internal PercyDriver(PercySeleniumDriver driver)
    {
      this.percySeleniumDriver = driver;
      setValues(this.percySeleniumDriver);
    }

    internal PercyDriver(IPercySeleniumDriver driver)
    {;
      this.percySeleniumDriver = driver;
      setValues(this.percySeleniumDriver);
    }

    public void Screenshot(String name, IEnumerable<KeyValuePair<string, object>>? options = null)
    {
      Percy.Screenshot(this.percySeleniumDriver.getRemoteWebDriver(), name, options);
    }
  }
}
