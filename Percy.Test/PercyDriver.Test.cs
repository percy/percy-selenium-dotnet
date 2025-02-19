using Moq;
using Xunit;
using System.Collections.Generic;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using Newtonsoft.Json;
using RichardSzalay.MockHttp;
using System.Net.Http;
using System;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace PercyIO.Selenium.Tests
{
  public class PercyDriverTest
  {
    private readonly Mock<IPercySeleniumDriver> remoteDriver;
    private Mock<PercyDriver> percyDriverMock;
    private readonly Dictionary<string, object> capabilities; // Changed from Mock to Dictionary

    public PercyDriverTest() {
      remoteDriver = new Mock<IPercySeleniumDriver>();
      capabilities = new Dictionary<string, object>
      {
          { "platformName", "win" },
          { "osVersion", "111.0" },
          { "browserName", "firefox" }
      };
      
      string url = "http://hub-cloud.browserstack.com/wd/hub/";
      remoteDriver.Setup(x => x.sessionId()).Returns("123");
      remoteDriver.Setup(x => x.GetHost()).Returns(url);
      remoteDriver.Setup(x => x.GetCapabilities()).Returns(capabilities); // Direct setup for GetCapabilities
      percyDriverMock = new Mock<PercyDriver>(remoteDriver.Object);
    }

    [Fact]
    public void postScreenshot()
    {
      Func<bool> oldEnabledFn = Percy.Enabled;
      Percy.Enabled = () => true;
      Percy.setSessionType("automate");
      // Setting Expectation
      Dictionary<string, object> expectedResponse = new Dictionary<string, object>()
      {
        { "sessionId", "123" },
        { "commandExecutorUrl", "http://hub-cloud.browserstack.com/wd/hub" },
        { "capabilities", capabilities }
      };
      // Testing Payload Function
      var actual = percyDriverMock.Object.getPayload();
      Assert.Equal(
          JsonConvert.SerializeObject(expectedResponse),
          JsonConvert.SerializeObject(actual)
      );

      var mockHttp = new MockHttpMessageHandler();
      var obj = new
      {
        success = true,
        version = "1.0",
      };
      mockHttp.Fallback.Respond(new HttpClient());
      mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
        .WithPartialContent("Screenshot 1")
        .Respond("application/json", JsonConvert.SerializeObject(obj));
      Percy.setHttpClient(new HttpClient(mockHttp));
      percyDriverMock.Object.Screenshot("Screenshot 1");
      mockHttp.VerifyNoOutstandingExpectation();
      Percy.Enabled = oldEnabledFn;
    }

    [Fact]
    public void postScreenshotWithSync()
    {
      Func<bool> oldEnabledFn = Percy.Enabled;
      Percy.Enabled = () => true;
      Percy.setSessionType("automate");
      // Setting Expectation
      Dictionary<string, object> expectedResponse = new Dictionary<string, object>()
      {
        { "sessionId", "123" },
        { "commandExecutorUrl", "http://hub-cloud.browserstack.com/wd/hub" },
        { "capabilities", capabilities }
      };
      // Testing Payload Function
      Assert.Equal(expectedResponse, percyDriverMock.Object.getPayload());

      var syncData = JObject.Parse("{'name': 'snapshot'}");
      var mockHttp = new MockHttpMessageHandler();
      var obj = new
      {
        success = true,
        version = "1.0",
        data = syncData
      };
      mockHttp.Fallback.Respond(new HttpClient());
      mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
        .WithPartialContent("Screenshot 1")
        .Respond("application/json", JsonConvert.SerializeObject(obj));
      Percy.setHttpClient(new HttpClient(mockHttp));
      Dictionary<string, object> options = new Dictionary<string, object>();
      options["sync"] = true;
      Assert.Equal(percyDriverMock.Object.Screenshot("Screenshot 1", options), syncData);
      mockHttp.VerifyNoOutstandingExpectation();
      Percy.Enabled = oldEnabledFn;
    }

    [Fact]
    public void postScreenshotWithOptions()
    {
      // Since mockHttp doesn't have functionality to mock response header,
      // Which is causing version check to break
      // Overiding function to return true and set Session Type
      Func<bool> oldEnabledFn = Percy.Enabled;
      Percy.Enabled = () => true;
      Percy.setSessionType("automate");
      var mockHttp = new MockHttpMessageHandler();
      var obj = new
      {
        success = true,
        version = "1.0",
      };
      mockHttp.Fallback.Respond(new HttpClient());

      Mock<IWebElement> element = new Mock<IWebElement>();
      Mock<IWebElement> considerElement = new Mock<IWebElement>();
      var ignoreElementList = new List<IWebElement> { element.Object };
      var considerElementList = new List<IWebElement> { considerElement.Object };
      remoteDriver.Setup(x => x.GetElementIdFromElement(element.Object)).Returns("dummy_element");
      remoteDriver.Setup(x => x.GetElementIdFromElement(considerElement.Object)).Returns("consider_dummy_element");
      Dictionary<string, object> options = new Dictionary<string, object>();
      options["ignore_region_selenium_elements"] = ignoreElementList;
      options["consider_region_selenium_elements"] = considerElementList;
      mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
        .WithPartialContent("dummy_element")
        .WithPartialContent("consider_dummy_element")
        .Respond("application/json", JsonConvert.SerializeObject(obj));
      Percy.setHttpClient(new HttpClient(mockHttp));
      percyDriverMock.Object.Screenshot("Screenshot 2", options);
      remoteDriver.Verify(d => d.GetElementIdFromElement(element.Object));

      mockHttp.VerifyNoOutstandingExpectation();
      Percy.Enabled = oldEnabledFn;
    }

    [Fact]
    public void postScreenshotThrowExceptionWithWeb()
    {
      Func<bool> oldEnabledFn = Percy.Enabled;
      Percy.Enabled = () => true;
      Percy.setSessionType("web");
      try {
         percyDriverMock.Object.Screenshot("Screenshot 1");
        Assert.Fail("Exception not raised");
      } catch (Exception error) {
        Assert.Equal("Invalid function call - Screenshot(). Please use Snapshot() function for taking screenshot. Screenshot() should be used only while using Percy with Automate. For more information on usage of PercySnapshot(), refer doc for your language https://www.browserstack.com/docs/percy/integrate/overview", error.Message);
      }
      Percy.Enabled = oldEnabledFn;
    }
  }
}
