# percy-selenium-dotnet
![Test](https://github.com/percy/percy-selenium-dotnet/workflows/Test/badge.svg)

[Percy](https://percy.io) visual testing for .NET Selenium.

## Development

Install/update `@percy/cli` dev dependency (requires Node 14+):

```sh-session
$ npm install --save-dev @percy/cli
```

Install dotnet SDK:

```sh-session
$ brew tap isen-ng/dotnet-sdk-versions
$ brew install --cask  dotnet-sdk5-0-400
$ dotnet --list-sdks
```

Install Mono:

```sh-session
$ brew install mono
$ mono --version 
```

Run tests:

```
npm run test
```

## Installation

npm install `@percy/cli` (requires Node 14+):

```sh-session
$ npm install --save-dev @percy/cli
```

Install the PercyIO.Selenium package (for example, with .NET CLI):

```sh-session
$ dotnet add package PercyIO.Selenium
```

## Usage

This is an example test using the `Percy.Snapshot` method.

``` csharp
using PercyIO.Selenium;

// ... other test code

FirefoxOptions options = new FirefoxOptions();
FirefoxDriver driver = new FirefoxDriver(options);
driver.Navigate().GoToUrl("http://example.com");
​
// take a snapshot
Percy.Snapshot(driver, ".NET example");

// snapshot options using an anonymous object
Percy.Snapshot(driver, ".NET anonymous options", new {
  widths = new [] { 600, 1200 }
});

// snapshot options using a dictionary-like object
Percy.Options snapshotOptions = new Percy.Options();
snapshotOptions.Add("minHeight", 1280);
Percy.Snapshot(driver, ".NET typed options", snapshotOptions);
```

Running the above normally will result in the following log:

```sh-session
[percy] Percy is not running, disabling snapshots
```

When running with [`percy
exec`](https://github.com/percy/cli/tree/master/packages/cli-exec#percy-exec), and your project's
`PERCY_TOKEN`, a new Percy build will be created and snapshots will be uploaded to your project.

```sh-session
$ export PERCY_TOKEN=[your-project-token]
$ percy exec -- [your test command]
[percy] Percy has started!
[percy] Created build #1: https://percy.io/[your-project]
[percy] Snapshot taken ".NET example"
[percy] Snapshot taken ".NET anonymous options"
[percy] Snapshot taken ".NET typed options"
[percy] Stopping percy...
[percy] Finalized build #1: https://percy.io/[your-project]
[percy] Done!
```

## Configuration

`void Percy.Snapshot(WebDriver driver, string name, object? options)`

- `driver` (**required**) - A selenium-webdriver driver instance
- `name` (**required**) - The snapshot name; must be unique to each snapshot
- `options` - An object containing various snapshot options ([see per-snapshot configuration options](https://docs.percy.io/docs/cli-configuration#per-snapshot-configuration))

## Running Percy on Automate
`Percy.Screenshot(driver, name, options)` [ needs @percy/cli 1.27.0-beta.0+ ];

This is an example test using the `Percy.Screenshot` method.

``` csharp
// ... other test code
// import
using PercyIO.Selenium;
class Program
{
  static void Main(string[] args)
  {

    // Add caps here
    RemoteWebDriver driver = new RemoteWebDriver(
      new Uri("https://hub-cloud.browserstack.com/wd/hub"),capabilities);
​

    // navigate to webpage
    driver.Navigate().GoToUrl("https://www.percy.io");

    // take screenshot
    Percy.Screenshot("dotnet screenshot-1");

    // quit driver
    driver.quit();
  }
}
```

- `driver` (**required**) - A Selenium driver instance
- `name` (**required**) - The screenshot name; must be unique to each screenshot
- `options` (**optional**) - There are various options supported by Percy.Screenshot to server further functionality.
    - `freezeAnimatedImage` - Boolean value by default it falls back to `false`, you can pass `true` and percy will freeze image based animations.
    - `freezeImageBySelectors` - List of selectors. Images will be freezed which are passed using selectors. For this to work `freezeAnimatedImage` must be set to true.
    - `freezeImageByXpaths` - List of xpaths. Images will be freezed which are passed using xpaths. For this to work `freezeAnimatedImage` must be set to true.
    - `percyCSS` - Custom CSS to be added to DOM before the screenshot being taken. Note: This gets removed once the screenshot is taken.
    - `ignoreRegionXpaths` - elements in the DOM can be ignored using xpath
    - `ignoreRegionSelectors` - elements in the DOM can be ignored using selectors.
    - `ignoreRegionSeleniumElements` - elements can be ignored using selenium_elements.
    - `customIgnoreRegions` - elements can be ignored using custom boundaries
    - `ignoreRegionXpaths` - List of xpaths. elements in the DOM can be ignored using xpath
    - `ignoreRegionSelectors` - List of selectors. elements in the DOM can be ignored using selectors.
    - `ignoreRegionSeleniumElements` - List of selenium web-element. elements can be ignored using selenium_elements.
    - `customIgnoreRegions` - List of custom objects. elements can be ignored using custom boundaries. Just passing a simple object for it like below.
      - Refer to example -
        - ```
          List<HashMap> customRegion = new ArrayList<>();
          HashMap<String, Integer> region1 = new HashMap<>();
          region1.put("top", 10);
          region1.put("bottom", 110);
          region1.put("right", 10);
          region1.put("left", 120);
          customRegion.add(region1);
          options.put("custom_ignore_regions", customRegion);
          ```
    - `considerRegionXpaths` - List of xpaths. elements in the DOM can be considered for diffing and will be ignored by Intelli Ignore using xpaths.
    - `considerRegionSelectors` - List of selectors. elements in the DOM can be considered for diffing and will be ignored by Intelli Ignore using selectors.
    - `considerRegionSeleniumElements` - List of selenium web-element. elements can be considered for diffing and will be ignored by Intelli Ignore using selenium_elements.
    - `customConsiderRegions` - List of custom objects. elements can be considered for diffing and will be ignored by Intelli Ignore using custom boundaries
      - Refer to example -
        - ```
          List<HashMap> customRegion = new ArrayList<>();
          HashMap<String, Integer> region2 = new HashMap<>();
          region2.put("top", 10);
          region2.put("bottom", 110);
          region2.put("right", 10);
          region2.put("left", 120);
          customRegion.add(region2);
          options.put("custom_consider_regions", customRegion);
          ```
        - Parameters:
          - `top` (int): Top coordinate of the consider region.
          - `bottom` (int): Bottom coordinate of the consider region.
          - `left` (int): Left coordinate of the consider region.
          - `right` (int): Right coordinate of the consider region.


### Creating Percy on automate build
Note: Automate Percy Token starts with `auto` keyword. The command can be triggered using `exec` keyword.
```sh-session
$ export PERCY_TOKEN=[your-project-token]
$ percy exec -- [dotnet test command]
[percy] Percy has started!
[percy] [Dotnet example] : Starting automate screenshot ...
[percy] Screenshot taken "Dotnet example"
[percy] Stopping percy...
[percy] Finalized build #1: https://percy.io/[your-project]
[percy] Done!
```

Refer to docs here: [Percy on Automate](https://docs.percy.io/docs/integrate-functional-testing-with-visual-testing)
