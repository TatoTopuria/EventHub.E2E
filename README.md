# EventHub.E2E

Cross-repo acceptance test for the EventHub platform: a Selenium scenario that drives the full
`messaging` stack (gateway + every service) and asserts the browse→book→pay→confirm saga path.
Carved from the monorepo via `git filter-repo` (history preserved).

## How it finds the stack

The fixture (`EventHubComposeFixture`) no longer owns a compose file — it points at the one in the
**EventHub.Deploy** repo, resolved in this order:

1. `EVENTHUB_COMPOSE_FILE` env var, if set.
2. A sibling `EventHub.Deploy/deploy/docker/docker-compose.yml` found by walking up from the test
   binary (works when both repos are checked out under a common parent).

It runs `docker compose ... up -d` (no `--build`) — the stack uses **pre-built service images**, not
source.

## Prerequisites to actually run it

1. **EventHub.Deploy** checked out (as a sibling, or set `EVENTHUB_COMPOSE_FILE`).
2. **Service images present** as `eventhub/<svc>:latest` (built/pushed by each service repo's CI, or
   built locally and tagged) — and the BaGetter feed up if any image build restores `EventHub.*`.
3. **Chrome + a matching ChromeDriver.** ⚠️ Known blocker: `Selenium.WebDriver.ChromeDriver` is
   pinned to **131** in `Directory.Packages.props` while local Chrome is **149** — the driver must
   match Chrome's major version. Bump that pin (or drop the package and let Selenium Manager
   auto-resolve) before running.

```bash
EVENTHUB_COMPOSE_FILE=../EventHub.Deploy/deploy/docker/docker-compose.yml \
  dotnet test tests/EventHub.E2E.SeleniumTests
```

The fixture self-skips (marks Docker unavailable) if the CLI is missing or the stack fails to come
up, so a missing prerequisite produces a clean skip rather than a hard failure.
