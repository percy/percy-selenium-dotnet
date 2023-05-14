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
  public class PercyDriverTest
  {
    [Fact]
    public void percyScreenshotTest()
    {
        Mock<IPercySeleniumDriver> _remoteDriver = new Mock<IPercySeleniumDriver>();
        Mock<ICapabilities> capabilities = new Mock<ICapabilities>();
        capabilities.Setup(x => x.GetCapability("platformName")).Returns("win");
        capabilities.Setup(x => x.GetCapability("browserName")).Returns("firefox");

        string url = "http://hub-cloud.browserstack.com/wd/hub";
        _remoteDriver.Setup(x => x.GetHost()).Returns(url);
        _remoteDriver.Setup(x => x.GetCapabilities()).Returns(capabilities.Object);

        var args = new JObject();
        var response = JObject.FromObject(new
        {
            success = true,
            deviceName = "Windows",
            osVersion = "10.0",
            buildHash = "dummy_build_hash",
            sessionHash = "dummy_session_hash"
        });

        _remoteDriver.Setup(x => x.sessionId()).Returns(new SessionId("xyz").ToString());
    
        PercyDriver percyDriver = new PercyDriver((RemoteWebDriver)_remoteDriver.Object);

    }
  }
}
