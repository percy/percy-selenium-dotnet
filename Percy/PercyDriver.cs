using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using System.Collections.Generic;

namespace PercyIO.Selenium
{
  public class PercyDriver
  {
    private Boolean isPercyEnabled;

    private IPercySeleniumDriver percySeleniumDriver;
    private String sessionId;
    public static Cache<string, object> cache = new Cache<string, object>();

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
        // { "sessionCapabilites", this.percySeleniumDriver.GetSessionDetails() },
      };
      return payload;
    }

    public PercyDriver(RemoteWebDriver driver)
    {
      // if(!Percy.Enabled()) return;
      // if(!Percy.isDriverValid(driver)) throw new Exception("Driver should be of type RemoteWebDriver");
      this.percySeleniumDriver = new PercySeleniumDriver(driver);
      setValues(this.percySeleniumDriver);
    }
  }
}
