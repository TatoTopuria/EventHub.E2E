namespace EventHub.E2E.SeleniumTests;

internal static class DockerRequirement
{
    private const string Message = "Docker or the EventHub compose stack is unavailable on this host; the E2E smoke test did not execute.";

    public static void SkipIfUnavailable(bool isDockerAvailable)
    {
        Assert.True(isDockerAvailable, Message);
    }
}