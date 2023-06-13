using Moq;
using Xunit;
using System.Collections.Generic;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using Newtonsoft.Json;
using RichardSzalay.MockHttp;
using System.Net.Http;

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
      capabilities.Setup(x => x.ToString()).Returns("{\"caps\": \"Dummy_capability\"}");
      string url = "http://hub-cloud.browserstack.com/wd/hub/";
      _remoteDriver.Setup(x => x.sessionId()).Returns("123");
      _remoteDriver.Setup(x => x.GetHost()).Returns(url);
      _remoteDriver.Setup(x => x.GetCapabilities()).Returns(capabilities.Object);
      // Setting Expectation
      Dictionary<string, object> expectedResponse = new Dictionary<string, object>()
      {
        { "sessionId", "123" },
        { "commandExecutorUrl", "http://hub-cloud.browserstack.com/wd/hub" },
        { "capabilities", capabilities.Object }
      };
      var percyDriverMock = new Mock<PercyDriver>(_remoteDriver.Object);
      // Testing Payload Function
      Assert.Equal(expectedResponse, percyDriverMock.Object.getPayload());

      var mockHttp = new MockHttpMessageHandler();
      var header = new Dictionary<string, string>();
      header["x-percy-core-version"] = "1.1";
      var obj = new
      {
        success = true,
        version = "1.0",
      };
      // Setup a respond for the user api
      mockHttp.Expect(HttpMethod.Get, "http://localhost:5338/percy/healthcheck")
        .Respond(header, "application/json", JsonConvert.SerializeObject(obj));
      mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
        .WithPartialContent("Screenshot 1")
        .Respond("application/json", JsonConvert.SerializeObject(obj));
      Percy.setHttpClient(new HttpClient(mockHttp));
      percyDriverMock.Object.Screenshot("Screenshot 1");

      Mock<IWebElement> element = new Mock<IWebElement>();
      var elementList = new List<IWebElement> { element.Object };
      _remoteDriver.Setup(x => x.GetElementIdFromElement(element.Object)).Returns("dummy_element");
      Dictionary<string, object> options = new Dictionary<string, object>();
      options["ignore_region_selenium_elements"] = elementList;
      mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
        .WithPartialContent("dummy_element")
        .Respond("application/json", JsonConvert.SerializeObject(obj));
      percyDriverMock.Object.Screenshot("Screenshot 2", options);
      _remoteDriver.Verify(d => d.GetElementIdFromElement(element.Object));

      mockHttp.VerifyNoOutstandingExpectation();
    }
  }
}
