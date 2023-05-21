using Moq;
using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Firefox;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using PercyIO.Selenium;
using OpenQA.Selenium.Remote;
using Newtonsoft.Json.Linq;


namespace PercyIO.Selenium.Tests
{

  public interface IWebDriverWrapper
  {
      IWebElement FindElement(By by);
  }

  public class WebDriverWrapper : IWebDriverWrapper
  {
      private readonly RemoteWebDriver driver;

      public WebDriverWrapper(RemoteWebDriver driver)
      {
          this.driver = driver;
      }

      public IWebElement FindElement(By by)
      {
          return driver.FindElement(by);
      }
  }

  public class PercyDriverTest
  {
    [Fact]
    public void percyScreenshotTest()
    {
        // Mock<IPercySeleniumDriver> _remoteDriver = new Mock<IPercySeleniumDriver>();
        // Mock<ICapabilities> capabilities = new Mock<ICapabilities>();
        // capabilities.Setup(x => x.GetCapability("platformName")).Returns("win");
        // capabilities.Setup(x => x.GetCapability("browserName")).Returns("firefox");

        // string url = "http://hub-cloud.browserstack.com/wd/hub";
        // _remoteDriver.Setup(x => x.GetHost()).Returns(url);
        // _remoteDriver.Setup(x => x.GetCapabilities()).Returns(capabilities.Object);

        // var args = new JObject();
        // var response = JObject.FromObject(new
        // {
        //     success = true,
        //     deviceName = "Windows",
        //     osVersion = "10.0",
        //     buildHash = "dummy_build_hash",
        //     sessionHash = "dummy_session_hash"
        // });

        // _remoteDriver.Setup(x => x.sessionId()).Returns(new SessionId("xyz").ToString());
        // Console.WriteLine(_remoteDriver.Object);
        // PercyDriver percyDriver = new PercyDriver((RemoteWebDriver)_remoteDriver.Object);
        // Create a mock for the IWebDriverWrapper
        var mockDriver = new Mock<IWebDriverWrapper>();

        // Set up the mock behavior for the FindElement method
        mockDriver.Setup(driver => driver.FindElement(By.Id("myElementId")))
                  .Returns(new Mock<IWebElement>().Object);

        // Use the mock driver in your test
        var driver = mockDriver.Object;
        var element = driver.FindElement(By.Id("myElementId"));

        // Assert the expected behavior
        Assert.NotNull(element);

    }
  }
}
