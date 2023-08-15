using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using System.Collections.Generic;

namespace PercyIO.Selenium
{
  public class PercyDriver
  {
    private IPercySeleniumDriver percySeleniumDriver;
    private String sessionId = "";
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
        { "commandExecutorUrl", this.percySeleniumDriver.GetHost().TrimEnd('/') },
        { "capabilities", this.percySeleniumDriver.GetCapabilities() },
      };
      return payload;
    }

    internal List<String> GetElementIdFromElements(List<IWebElement> elements) 
    {
      List<string> ignoredElementsArray = new List<string>();
      for (int index = 0; index < elements.Count; index++)
      {
          ignoredElementsArray.Add(percySeleniumDriver.GetElementIdFromElement(elements[index]));
      }
      return ignoredElementsArray;
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
      Percy.Screenshot(this, name, options);
    }
  }
}
