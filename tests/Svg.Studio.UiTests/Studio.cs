using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Mac;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Svg.Studio.UiTests;

/// <summary>A published Svg.Studio.app, launched by Appium's mac2 driver and driven through accessibility.</summary>
/// <remarks>
/// One process per test: Studio's state is the process's, so a shared one would make each test
/// depend on the ones before. Skipped unless <c>SVG_STUDIO_UI_APP</c> names the bundle, which only
/// <c>.github/workflows/studio-ui.yml</c> sets: a run takes over the pointer and the keyboard of
/// whatever machine it is on.
/// </remarks>
internal sealed class Studio : IDisposable
{
    private const string BundleId = "com.svgskia.studio";

    private static readonly string? App = Environment.GetEnvironmentVariable("SVG_STUDIO_UI_APP");

    private Studio(string data, string[] arguments)
    {
        Data = data;
        Driver = Start(arguments);
    }

    /// <summary>The folder handed to Studio as <c>SVG_STUDIO_DATA</c>, in place of the user's own.</summary>
    public string Data { get; }

    public MacDriver Driver { get; private set; }

    public static Studio Launch(params string[] arguments)
    {
        Assert.SkipUnless(App is { Length: > 0 }, "SVG_STUDIO_UI_APP does not name a Svg.Studio.app; the suite runs in CI (studio-ui.yml), since it takes over the pointer and keyboard.");

        return new Studio(Directory.CreateTempSubdirectory("svg-studio-ui-").FullName, arguments);
    }

    /// <summary>Writes a file into <see cref="Data"/>, for a test to open.</summary>
    public string Write(string name, string text)
    {
        var path = Path.Combine(Data, name);

        File.WriteAllText(path, text);

        return path;
    }

    /// <summary>Writes a project into <see cref="Data"/>: a folder with an empty <c>studio.xml</c> and one <c>.svg</c> per drawing.</summary>
    /// <remarks>Written by hand rather than by Studio, which only saves a document it already holds.</remarks>
    public string Package(string name, params (string Drawing, string Text)[] drawings)
    {
        var path = Path.Combine(Data, name);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "studio.xml"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<studio>\n</studio>\n");

        foreach (var (drawing, text) in drawings)
        {
            File.WriteAllText(Path.Combine(path, drawing + ".svg"), text);
        }

        return path;
    }

    /// <summary>Ends the process the way a crash does: nothing it would do on closing gets done.</summary>
    public void Kill()
    {
        foreach (var process in Process.GetProcessesByName("Svg.Studio").Where(Ours))
        {
            process.Kill();
            process.WaitForExit();
        }

        Quit();
    }

    /// <summary>Starts Studio again on the same <see cref="Data"/>.</summary>
    public void Relaunch(params string[] arguments)
    {
        Quit();
        Driver = Start(arguments);
    }

    /// <summary>The front window's title, which is where Studio says what is open and whether it is saved.</summary>
    public string Title => Until(() => Windows.FirstOrDefault(), "a window").GetAttribute("title") ?? string.Empty;

    public IReadOnlyList<IWebElement> Windows => Driver.FindElements(Chain("XCUIElementTypeWindow"));

    /// <summary>The window with this title, once there is one.</summary>
    public IWebElement Window(string title)
        => Until(() => Driver.FindElements(Chain($"**/XCUIElementTypeWindow[`title == '{title}'`]")).FirstOrDefault(), $"the window '{title}'");

    /// <summary>The control an <c>x:Name</c> names, which Avalonia hands macOS as its identifier.</summary>
    public IWebElement Find(string name)
        => Until(() => Driver.FindElements(MobileBy.AccessibilityId(name)).FirstOrDefault(e => e.Displayed), $"'{name}'");

    public bool Shows(string name) => Driver.FindElements(MobileBy.AccessibilityId(name)).Any(e => e.Displayed);

    public void Click(string name) => Click(Find(name));

    /// <summary>Clicks with the real pointer, so only once Studio is the app in front.</summary>
    public void Click(IWebElement element)
    {
        Front();
        element.Click();
    }

    /// <summary>The project tree's row for a drawing or group, rather than a board caption with the same name.</summary>
    public IWebElement Row(string name)
        => Until(() => Driver.FindElements(Chain($"**/XCUIElementTypeOutline[`identifier == 'ProjectTree'`]/**/XCUIElementTypeStaticText[`value == '{name}'`]")).FirstOrDefault(), $"the row '{name}'");

    /// <summary>Clicks with ⌘ held, which selects a row without opening what it holds.</summary>
    /// <remarks>
    /// By mac2's own click at the element's middle: WebDriverAgentMac refuses the W3C chain of a key
    /// held over a click, which opens with a pause before any pointer move.
    /// </remarks>
    public void CommandClick(IWebElement element)
    {
        Front();
        Driver.ExecuteScript("macos: click", new Dictionary<string, object>
        {
            ["x"] = element.Location.X + (element.Size.Width / 2),
            ["y"] = element.Location.Y + (element.Size.Height / 2),
            ["keyModifierFlags"] = CommandKey
        });
    }

    /// <summary>A button by what it says, for the ones built in code without a name.</summary>
    public IWebElement Button(string title)
        => Until(() => Driver.FindElements(Chain($"**/XCUIElementTypeButton[`title == '{title}'`]")).FirstOrDefault(), $"the button '{title}'");

    /// <summary>Anything that says <paramref name="text"/>, for Avalonia's popups, whose items macOS gives no settled type.</summary>
    public IWebElement Labelled(string text)
        => Until(() => Driver.FindElements(Saying(text)).FirstOrDefault(e => e.Displayed), $"'{text}'");

    public bool Says(string text) => Driver.FindElements(Saying(text)).Any(e => e.Displayed);

    /// <summary>Picks an item from the menu bar, which is where macOS puts Studio's NativeMenu.</summary>
    public void Menu(string menu, string item)
    {
        Click(Until(() => Driver.FindElements(Chain($"**/XCUIElementTypeMenuBarItem[`title == '{menu}'`]")).FirstOrDefault(), $"the menu '{menu}'"));
        Click(MenuItem(menu, item));
    }

    /// <summary>An item of a menu bar menu, opening the menu if it is not.</summary>
    public IWebElement MenuItem(string menu, string item)
        => Until(() => Driver.FindElements(Chain($"**/XCUIElementTypeMenuBarItem[`title == '{menu}'`]/**/XCUIElementTypeMenuItem[`title == '{item}'`]")).FirstOrDefault(), $"'{menu} → {item}'");

    /// <summary>The items of a menu bar menu, which has to be open for them to be read.</summary>
    public IReadOnlyList<string> MenuItems(string menu)
    {
        Click(Until(() => Driver.FindElements(Chain($"**/XCUIElementTypeMenuBarItem[`title == '{menu}'`]")).FirstOrDefault(), $"the menu '{menu}'"));

        var items = Driver.FindElements(Chain($"**/XCUIElementTypeMenuBarItem[`title == '{menu}'`]/**/XCUIElementTypeMenuItem"))
            .Select(e => e.GetAttribute("title"))
            .ToList();

        Press(Keys.Escape);

        return items;
    }

    /// <summary>A key with ⌘, as the menu's key equivalents are pressed.</summary>
    public void Command(string key, bool shift = false)
    {
        Front();
        Driver.ExecuteScript("macos: keys", new Dictionary<string, object>
        {
            ["keys"] = new object[] { new Dictionary<string, object> { ["key"] = key, ["modifierFlags"] = CommandKey | (shift ? ShiftKey : 0) } }
        });
    }

    /// <summary>Types into whatever in Studio has the keyboard.</summary>
    public void Press(string keys)
    {
        Front();
        new OpenQA.Selenium.Interactions.Actions(Driver).SendKeys(keys).Perform();
    }

    /// <summary>Asks until the answer is something, or saves what the screen showed and fails.</summary>
    /// <remarks>Twenty seconds: a cold launch of the bundle takes a few, and the mac2 driver's each query a few hundred ms.</remarks>
    public T Until<T>(Func<T?> probe, string what, double seconds = 20d)
        where T : class
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                if (probe() is { } found)
                {
                    return found;
                }
            }
            catch (WebDriverException) when (clock.Elapsed.TotalSeconds < seconds)
            {
                // An element can go stale between being found and being read while the tree updates.
            }

            if (clock.Elapsed.TotalSeconds >= seconds)
            {
                Assert.Fail($"{what} never appeared. What the screen showed is in {Evidence()}.");
            }

            Thread.Sleep(200);
        }
    }

    public void Until(Func<bool> done, string what, double seconds = 20d)
        => Until(() => done() ? (object)true : null, what, seconds);

    public void Dispose()
    {
        Quit();

        try
        {
            Directory.Delete(Data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Brings Studio forward and makes sure it came, before anything is clicked or typed.</summary>
    /// <remarks>
    /// The pointer and the keyboard are the machine's: with another app in front, a click lands in
    /// it and ⌘Q quits it. A focus taken away mid-run fails the test rather than typing elsewhere.
    /// Asked again on every try: an activation that silently does not take, leaving Finder with the
    /// menu bar, is what hosted runners were seen doing (tig/winprint 692f794).
    /// </remarks>
    private void Front()
        => Until(() =>
        {
            var app = new Dictionary<string, object> { ["bundleId"] = BundleId };

            Driver.ExecuteScript("macos: activateApp", app);

            return Convert.ToInt64(Driver.ExecuteScript("macos: queryAppState", app), CultureInfo.InvariantCulture) == RunningForeground;
        }, "Studio in front", 10d);

    private MacDriver Start(string[] arguments)
    {
        var options = new AppiumOptions { PlatformName = "mac", AutomationName = "mac2" };

        options.AddAdditionalAppiumOption("bundleId", BundleId);
        options.AddAdditionalAppiumOption("appPath", App!);
        options.AddAdditionalAppiumOption("arguments", arguments);
        options.AddAdditionalAppiumOption("environment", new Dictionary<string, object> { ["SVG_STUDIO_DATA"] = Data });

        return new MacDriver(new Uri("http://127.0.0.1:4723"), options, TimeSpan.FromMinutes(3));
    }

    /// <summary>Ends the session, which ends the app with it.</summary>
    private void Quit()
    {
        try
        {
            Driver.Quit();
        }
        catch (WebDriverException)
        {
            // Already gone, as after Kill.
        }
    }

    /// <summary>The bundle these tests launched, rather than a Studio somebody is using.</summary>
    private static bool Ours(Process process)
    {
        try
        {
            return process.MainModule?.FileName.StartsWith(App!, StringComparison.Ordinal) == true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>A screenshot and the accessibility tree, beside the test assembly.</summary>
    private string Evidence()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "failures", $"{TestContext.Current.Test?.TestDisplayName ?? "test"}-{DateTime.Now:HHmmss}");

        try
        {
            Directory.CreateDirectory(folder);
            Driver.GetScreenshot().SaveAsFile(Path.Combine(folder, "screen.png"));
            File.WriteAllText(Path.Combine(folder, "source.xml"), Driver.PageSource);
        }
        catch (WebDriverException)
        {
        }

        return folder;
    }

    private static By Saying(string text) => new Locator("-ios predicate string", $"title == '{text}' OR label == '{text}' OR value == '{text}'");

    private static By Chain(string chain) => new Locator("-ios class chain", chain);

    /// <summary>XCUIKeyModifierShift and XCUIKeyModifierCommand, as <c>macos: keys</c> takes modifiers.</summary>
    private const int ShiftKey = 1 << 1;
    private const int CommandKey = 1 << 4;

    /// <summary>XCUIApplicationStateRunningForeground, as <c>macos: queryAppState</c> answers.</summary>
    private const long RunningForeground = 4;

    /// <summary>The mac2 driver's class chain and predicate string, which the client offers only under their iOS names.</summary>
    private sealed class Locator : By
    {
        public Locator(string mechanism, string criteria)
            : base(mechanism, criteria)
        {
        }
    }
}
