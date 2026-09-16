using FluentAssertions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace EventHub.E2E.SeleniumTests;

/// <summary>
/// F8 end-to-end smoke test — drives the WebUI through register → login → browse → reserve seat A1
/// and verifies (a) the booking lands as <c>Confirmed</c> via the saga-driven payment path, and
/// (b) the SignalR client received a <c>SeatReserved</c> push during the flow.
/// </summary>
/// <remarks>
/// Skipped cleanly when the host has no Docker (see <see cref="EventHubComposeFixture.IsDockerAvailable"/>).
/// The single test covers the entire critical path because each Selenium-driven scenario is
/// expensive (multi-minute compose startup, headless Chrome boot, full saga round-trip).
/// </remarks>
public sealed class BrowseBookPayScenarioTests : IClassFixture<EventHubComposeFixture>, IDisposable
{
    private static readonly TimeSpan PageWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SagaWaitTimeout = TimeSpan.FromSeconds(60);

    private readonly EventHubComposeFixture _fixture;
    private readonly IWebDriver? _driver;

    public BrowseBookPayScenarioTests(EventHubComposeFixture fixture)
    {
        _fixture = fixture;
        if (!_fixture.IsDockerAvailable)
        {
            _driver = null;
            return;
        }

        var options = new ChromeOptions();
        options.AddArgument("--headless=new");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--window-size=1280,900");
        // Some CI images mount tmpfs with limited inodes; this avoids a crash on `/dev/shm` writes.
        options.AddArgument("--disable-gpu");

        _driver = new ChromeDriver(options);
        _driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
    }

    [Fact]
    public void Browse_Book_Pay_Should_Reach_Confirmed_And_Push_SeatReserved()
    {
        if (!_fixture.IsDockerAvailable || _driver is null)
        {
            DockerRequirement.SkipIfUnavailable(_fixture.IsDockerAvailable);
            Assert.True(_driver is not null, "The Selenium driver was not created; the EventHub E2E smoke test did not execute.");
        }

        _driver.Navigate().GoToUrl(_fixture.WebUiBaseUrl);

        // 1. Register a fresh user (deterministic-ish unique email per run).
        var email = $"e2e-{Guid.NewGuid():N}@eventhub.test";
        const string password = "E2EPassword!123";

        TypeInto("email-input", email);
        TypeInto("password-input", password);
        Click("register-button");

        WaitForCondition(driver =>
        {
            var status = driver.FindElement(By.Id("auth-status")).Text;
            return status.StartsWith("Registered as", StringComparison.OrdinalIgnoreCase);
        }, "auth-status to confirm registration");

        // 2. Wait for the events list to populate (catalog seeds three on startup).
        var eventsList = WaitFor(driver =>
        {
            var list = driver.FindElement(By.Id("events-list"));
            var items = list.FindElements(By.TagName("li"));
            return items.Count > 0 ? list : null;
        }, "events list to populate");

        var firstEvent = eventsList!.FindElements(By.TagName("li")).First();
        firstEvent.Click();

        // 3. Pick seat A1 — every seeded event includes it.
        var seatA1 = WaitFor(driver =>
        {
            var seats = driver.FindElement(By.Id("seats-list")).FindElements(By.TagName("li"));
            return seats.FirstOrDefault(seat =>
                string.Equals(seat.Text, "A1", StringComparison.Ordinal)
                && !seat.GetAttribute("class").Contains("reserved", StringComparison.Ordinal));
        }, "seat A1 to be available");

        seatA1!.Click();
        Click("reserve-button");

        WaitForCondition(driver =>
        {
            var status = driver.FindElement(By.Id("reserve-status")).Text;
            return status.StartsWith("Reserved bookingId=", StringComparison.OrdinalIgnoreCase);
        }, "reserve-status to confirm reservation");

        // 4. Saga drives Reserve → Charge → Confirm. UI polls /api/bookings until status flips.
        var bookingStatus = WaitFor(driver =>
        {
            var element = driver.FindElement(By.Id("booking-status"));
            return string.Equals(element.GetAttribute("data-confirmed"), "true", StringComparison.OrdinalIgnoreCase)
                ? element
                : null;
        }, "booking-status to reach Confirmed", SagaWaitTimeout);

        bookingStatus!.Text.Should().Contain("Confirmed", "saga must drive the booking to confirmed");

        // 5. SignalR push — the realtime hub must have notified the browser that seat A1 was reserved.
        var signalRBadge = WaitFor(driver =>
        {
            var element = driver.FindElement(By.Id("signalr-status"));
            return string.Equals(element.GetAttribute("data-seat-reserved"), "true", StringComparison.OrdinalIgnoreCase)
                ? element
                : null;
        }, "SignalR client to observe SeatReserved push", SagaWaitTimeout);

        signalRBadge.Should().NotBeNull("realtime hub must have pushed SeatReserved to the browser");
    }

    private void TypeInto(string id, string text)
    {
        var element = _driver!.FindElement(By.Id(id));
        element.Clear();
        element.SendKeys(text);
    }

    private void Click(string id) => _driver!.FindElement(By.Id(id)).Click();

    private TResult? WaitFor<TResult>(Func<IWebDriver, TResult?> condition, string description, TimeSpan? timeout = null)
        where TResult : class
    {
        var wait = new WebDriverWait(_driver, timeout ?? PageWaitTimeout)
        {
            PollingInterval = TimeSpan.FromMilliseconds(250)
        };
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        try
        {
            return wait.Until(condition);
        }
        catch (WebDriverTimeoutException)
        {
            throw new WebDriverTimeoutException($"Timed out waiting for {description}.");
        }
    }

    private void WaitForCondition(Func<IWebDriver, bool> predicate, string description, TimeSpan? timeout = null)
    {
        var wait = new WebDriverWait(_driver, timeout ?? PageWaitTimeout)
        {
            PollingInterval = TimeSpan.FromMilliseconds(250)
        };
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        try
        {
            wait.Until(predicate);
        }
        catch (WebDriverTimeoutException)
        {
            throw new WebDriverTimeoutException($"Timed out waiting for {description}.");
        }
    }

    public void Dispose()
    {
        _driver?.Quit();
        _driver?.Dispose();
    }
}
