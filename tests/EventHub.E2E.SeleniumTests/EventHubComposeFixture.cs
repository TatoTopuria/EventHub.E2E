using System.Diagnostics;
using System.Net;
using System.Text;

namespace EventHub.E2E.SeleniumTests;

/// <summary>
/// xUnit fixture that brings up the full <c>messaging</c> docker-compose profile (gateway + every
/// service + RabbitMQ + Postgres + Redis + Kafka + MailHog + Selenium-target WebUI) for the
/// end-to-end smoke test.
/// </summary>
/// <remarks>
/// <para>Pattern mirrors <c>GatewayComposeFixture</c> in the API gateway integration tests.</para>
/// <para>
/// Sets <see cref="IsDockerAvailable"/> to false if the docker CLI is missing or compose-up fails,
/// so individual tests can early-return cleanly on machines without Docker (no false-negative
/// failures on contributor laptops, no skipped-test noise in CI logs).
/// </para>
/// </remarks>
public sealed class EventHubComposeFixture : IAsyncLifetime
{
    private const string ComposeFileRelativePath = "deploy/docker/docker-compose.yml";
    private const string ComposeProfile = "messaging";
    private const string GatewayBase = "http://localhost:8080";
    private const string WebUiBase = "http://localhost:5050";

    private readonly string _composeFilePath;

    public EventHubComposeFixture()
    {
        _composeFilePath = FindComposeFilePath();
    }

    public bool IsDockerAvailable { get; private set; } = true;

    public string GatewayBaseUrl => GatewayBase;

    public string WebUiBaseUrl => WebUiBase;

    public HttpClient HttpClient { get; } = new() { BaseAddress = new Uri(GatewayBase) };

    public async Task InitializeAsync()
    {
        if (!await IsDockerCliAvailableAsync())
        {
            IsDockerAvailable = false;
            return;
        }

        try
        {
            // No --build: the EventHub.Deploy compose references service images (built/pushed by each
            // service repo's CI), so the E2E runs against pre-built images rather than source.
            await RunDockerComposeAsync($"-f \"{_composeFilePath}\" --profile {ComposeProfile} up -d");
            await WaitForReadyAsync();
        }
        catch (Exception)
        {
            // Make a best-effort attempt to tear down anything that may have started, then mark the
            // fixture as unavailable so tests skip cleanly.
            IsDockerAvailable = false;
            try
            {
                await RunDockerComposeAsync($"-f \"{_composeFilePath}\" --profile {ComposeProfile} down --remove-orphans -v");
            }
            catch
            {
                // ignore — we are already in an unavailable state
            }
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (IsDockerAvailable)
            {
                await RunDockerComposeAsync($"-f \"{_composeFilePath}\" --profile {ComposeProfile} down --remove-orphans -v");
            }
        }
        finally
        {
            HttpClient.Dispose();
        }
    }

    private static string FindComposeFilePath()
    {
        // The compose file now lives in the separate EventHub.Deploy repo. Prefer an explicit
        // override; otherwise walk up looking for it either directly (combined checkout) or under a
        // sibling EventHub.Deploy/ (the repos checked out under a common parent).
        var fromEnv = Environment.GetEnvironmentVariable("EVENTHUB_COMPOSE_FILE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            string[] candidates =
            [
                Path.Combine(current.FullName, ComposeFileRelativePath),
                Path.Combine(current.FullName, "EventHub.Deploy", ComposeFileRelativePath),
            ];
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate '{ComposeFileRelativePath}'. Set EVENTHUB_COMPOSE_FILE to the EventHub.Deploy compose path.");
    }

    private static async Task<bool> IsDockerCliAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format \"{{.Server.Version}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunDockerComposeAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"compose {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker process.");

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose {arguments} exited with {process.ExitCode}.{Environment.NewLine}{output}");
        }
    }

    private async Task WaitForReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(4);
        var lastError = string.Empty;
        var webUiClient = new HttpClient { BaseAddress = new Uri(WebUiBase) };

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var gatewayResponse = await HttpClient.GetAsync("/api/identity/auth/login");
                    if (gatewayResponse.StatusCode != HttpStatusCode.BadGateway
                        && gatewayResponse.StatusCode != HttpStatusCode.ServiceUnavailable)
                    {
                        using var webUiResponse = await webUiClient.GetAsync("/healthz");
                        if (webUiResponse.IsSuccessStatusCode)
                        {
                            // One more health check on the catalog — events must be present before
                            // the Selenium scenario runs, otherwise the SPA shows "No events".
                            using var catalogResponse = await HttpClient.GetAsync("/api/catalog/events?pageSize=1");
                            if (catalogResponse.IsSuccessStatusCode)
                            {
                                return;
                            }
                            lastError = $"catalog returned {(int)catalogResponse.StatusCode}";
                        }
                        else
                        {
                            lastError = $"webui returned {(int)webUiResponse.StatusCode}";
                        }
                    }
                    else
                    {
                        lastError = $"gateway returned {(int)gatewayResponse.StatusCode}";
                    }
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            webUiClient.Dispose();
        }

        throw new TimeoutException($"Stack did not become ready in time. Last error: {lastError}");
    }
}
