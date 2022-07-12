# percy-selenium-dotnet
![Test](https://github.com/percy/percy-selenium-dotnet/workflows/Test/badge.svg)

[Percy](https://percy.io) visual testing for .NET Selenium.

## Installation

npm install `@percy/cli` (requires Node 14+):

```sh-session
$ npm install --save-dev @percy/cli
```

Install the Percy.Selenium package (for example, with .NET CLI):

```ssh-session
$ dotnet add package Percy.Selenium
```

## Usage

This is an example test using the `Percy.Snapshot` method.

``` csharp
using Percy.Selenium;

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
