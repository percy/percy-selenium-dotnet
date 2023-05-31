using Moq;
using Xunit;
using System.Collections.Generic;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using Newtonsoft.Json.Linq;


namespace PercyIO.Selenium.Tests
{
  public class PercyDriverTest
  {
    [Fact]
    public void getDriverDetails()
    {
      // This function tests for payload and driverDetails
      Mock<IPercySeleniumDriver> _remoteDriver = new Mock<IPercySeleniumDriver>();
      Mock<ICapabilities> capabilities = new Mock<ICapabilities>();
      capabilities.Setup(x => x.GetCapability("platformName")).Returns("win");
      capabilities.Setup(x => x.GetCapability("osVersion")).Returns("111.0");
      capabilities.Setup(x => x.GetCapability("browserName")).Returns("firefox");
      string url = "http://hub-cloud.browserstack.com/wd/hub";
      _remoteDriver.Setup(x => x.sessionId()).Returns("123");
      _remoteDriver.Setup(x => x.GetHost()).Returns(url);
      _remoteDriver.Setup(x => x.GetCapabilities()).Returns(capabilities.Object);
      // Setting Expectation
      Dictionary<string, object> expectedResponse = new Dictionary<string, object>()
      {
        { "sessionId", "123" },
        { "commandExecutorUrl", url },
        { "capabilities", capabilities.Object }
      };
      var percyDriverMock = new Mock<PercyDriver>(_remoteDriver.Object);
      // Testing Payload Function
      Assert.Equal(expectedResponse, percyDriverMock.Object.getPayload());
    }
  }
}
