<div align="center">

<img src="https://github.com/pinkroosterai/Persistify/raw/main/img/logo_transparent.png" alt="PinkRooster Logo" width="200" />

# WebLens

**Web search and web pages as clean Markdown, for AI agents.**

A small, authenticated API that searches through your own [SearXNG](https://github.com/searxng/searxng) instances and
fetches any page as its main content in Markdown, over REST and [MCP](https://modelcontextprotocol.io).

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-Compatible-green)](https://modelcontextprotocol.io/)
[![CI](https://github.com/pinkroosterai/PinkRooster.WebLens/actions/workflows/ci.yml/badge.svg)](https://github.com/pinkroosterai/PinkRooster.WebLens/actions/workflows/ci.yml)

[API](#api) · [MCP](#mcp) · [How it works](#how-it-works) · [Security model](#security-model-ssrf) · [Run locally](#running-it-locally) · [Deploy](#deploying-with-docker)

</div>

---

WebLens does two things:

- **Search** the web through one or more SearXNG instances you run or trust.
- **Fetch** a web page and return its main content as clean Markdown. It tries a plain HTTP GET first and only renders
  in headless Chromium (via Playwright) when a page needs JavaScript.

Both are available as REST endpoints and as tools on the built-in MCP server. They share API keys, per-key rate
limits, the cache and the SSRF defence. It targets .NET 10 and runs as one container next to a small egress proxy, a
DNS resolver and Valkey.

## API

Every request needs an API key in the `X-Api-Key` header. Each key has scopes (`search`, `fetch`) and a rate-limit
profile.

| Endpoint | What it does |
| --- | --- |
| `POST /v1/search` | Search. Body: `query`, and optionally `categories`, `language`, `page`, `timeRange`, `safeSearch`, `engines`, `limit`. |
| `POST /v1/fetch` | Fetch a page as Markdown. Body: `url`, and optionally `options` (`contentSelector`, `readySelector`, `excludeSelectors`, `includeLinks`, `includeImages`, `maxChars`, `timeoutSeconds`, `bypassCache`). |
| `POST /mcp` | The MCP server (see below). |
| `GET /health/live`, `/health/ready` | Liveness and readiness, anonymous. |
| `GET /health/details` | Per-component health, needs a key. |
| `GET /openapi/v1.json` | The OpenAPI 3.1 document (anonymous in Development, needs a key elsewhere). |

```bash
curl -s http://127.0.0.1:5195/v1/fetch \
  -H 'X-Api-Key: weblens-dev-key' -H 'content-type: application/json' \
  -d '{"url":"https://example.com/"}'
```

Errors are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) `application/problem+json` with stable
`urn:weblens:problem:*` types, so a client can switch on `type`. Status codes follow one rule set:

- `429` only ever means your key hit its own rate limit.
- `422` means the target site refused (for example a bot challenge or a login wall). WebLens never tries to solve or
  get around CAPTCHAs or WAF challenges.
- `502`/`504` mean the target or the SearXNG upstream failed or timed out.
- `503` means WebLens itself is out of capacity or not healthy.

## MCP

`POST /mcp` offers `search` and `fetch` as MCP tools over streamable HTTP. It accepts the key as `X-Api-Key` or as
`Authorization: Bearer <key>`. To add it to Claude Code:

```bash
claude mcp add --transport http weblens https://weblens.example.com/mcp --header "X-Api-Key: $WEBLENS_API_KEY"
```

Long pages are returned in parts, so a large document stays within the client's output limits.

## How it works

One host (`PinkRooster.WebLens.Api`), two modules that know nothing about HTTP or each other:

- **`PinkRooster.WebLens.Search`** sends the query to the configured SearXNG instances. It picks an instance by
  health and capabilities (read from SearXNG's `/config`), fails over to the next one, and cools down instances that
  rate-limit or refuse. SearXNG's JSON is mapped onto WebLens's own stable model, and results are sanitised. Choose
  engines with SearXNG's bang syntax (`!wikipedia`) or the `engines` field.
- **`PinkRooster.WebLens.Fetch`** loads the page (plain HTTP first, Chromium when needed), finds the main content
  (SmartReader, a caller's `contentSelector`, or a listing extractor for index pages such as news front pages), and
  converts it with ReverseMarkdown. It detects bot challenges and block pages and reports them as `422`.

Anonymous fetches and searches are cached in memory and, when configured, in Valkey. A cache hit costs no rate-limit
token. Time budgets nest: request timeout > module budget > each attempt, so the innermost one fires first and the
caller gets a typed `504`.

## Security model: SSRF

Fetching arbitrary URLs from inside your network is dangerous: a caller could ask for `http://169.254.169.254/` or an
internal admin page. WebLens defends in four layers. **Only L3 is a security boundary**; L1 and L2 are early,
friendly refusals.

- **L1, URL guard:** scheme, port, userinfo and denied address ranges are checked before any network work.
- **L2, route guard:** every browser request is checked again inside Chromium. It does not see redirect hops, so the
  redirect chain is checked after navigation too.
- **L3, egress proxy:** the only way out. Every browser context and the HTTP path go through
  [Smokescreen](https://github.com/stripe/smokescreen) (`deploy/egress-proxy`). It resolves DNS itself and refuses
  loopback, private, link-local, CGNAT and IPv4-embedding IPv6 addresses. Outside Development the host refuses to
  start without it.
- **L4, process hardening:** non-root, Chromium's sandbox kept on, a minimal browser environment so no secrets are
  inherited, read-only file system, a seccomp profile.

**L3 only works if the app has no other route out.** In `compose.example.yml` the app is attached to internal Docker
networks only, and the proxy and DNS resolver are the only containers that reach the internet. Keep it that way. The
proxy's README explains the flags you must never add.

## Running it locally

You need the .NET 10 SDK and a SearXNG instance with the JSON format enabled (`search.formats` includes `json` in its
`settings.yml`). The Development configuration expects it at `http://localhost:8080`, allows private networks and
runs without the egress proxy. **Never use it for anything reachable by others.**

```bash
dotnet build
cd src/PinkRooster.WebLens.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://127.0.0.1:5195
```

The Development API key is `weblens-dev-key`. Chromium is needed for pages that require JavaScript. After a build,
install it with `pwsh tests/PinkRooster.WebLens.Fetch.BrowserTests/bin/Debug/net10.0/playwright.ps1 install chromium`.

## Deploying with Docker

CI publishes three images to GitHub Container Registry: `ghcr.io/pinkroosterai/weblens`, `weblens-egress-proxy` and
`weblens-resolver`, each tagged `current` and with the commit's short SHA.

```bash
cp compose.example.yml compose.yml
cp .env.example .env               # then fill it in
docker network create --internal weblens-ingress   # attach your reverse proxy here
docker network create --internal weblens-search    # attach SearXNG here
docker compose pull && docker compose up -d
```

Set `WEBLENS_API_KEY_SHA256` to the SHA-256 of a key you generate: `printf '%s' "$KEY" | sha256sum`. Only the hash is
stored. Roll back by setting `IMAGE_TAG` to an earlier short SHA.

### Local builds (private deploy)

To test changes from the working tree in a production-like hardened stack without pushing to GitHub or a registry, use the Compose override file (`compose.dev.yml`) or the convenience script:

```bash
docker compose -f compose.yml -f compose.dev.yml up -d --build
# or: ./deploy/dev.sh
```

This builds the `app`, `proxy` and `resolver` images locally and tags them `:local`. To switch back to the CI-published images from GHCR:

```bash
docker compose up -d --pull always
# or: ./deploy/dev.sh reset
```

`deploy/load-check.sh` checks capacity and graceful shutdown against the running stack. `deploy/benchmark.sh` times
uncached fetches of a fixed page list.

## Configuration

Everything binds from `WebLens:*` (environment variables use `__`, for example `WebLens__Api__Keys__0__Sha256`) and is
validated at start-up. The host refuses to start on a bad value and says why.

| Section | Holds |
| --- | --- |
| `WebLens:Api` | `Keys` (id, `Sha256`, `Scopes`, `RateLimitProfile`), `RateLimitProfiles`, `ValkeyConnectionString` (required outside Development). |
| `WebLens:Search` | `Instances` (name, `BaseUri`, priority), routing, timeouts, transport, capability and cache settings. |
| `WebLens:Fetch` | `Browser` (concurrency, sandbox, locale, recycling), `Security` (`EgressProxy:Server`, `RequireEgressProxy`), budgets, per-origin limits, extraction and Markdown settings, cache. |

The defaults live in `src/PinkRooster.WebLens.*/*Options.cs`. Key hashes and the Valkey connection string are secrets:
put them in the environment or a secret store, never in `appsettings.json`.

## Tests

```bash
dotnet test                                                          # everything
dotnet test --project tests/PinkRooster.WebLens.Fetch.BrowserTests   # real Chromium against a local fixture site
```

- The browser tests need the Chromium revision the pinned `Microsoft.Playwright` expects (see above to install it).
- The SSRF suite and the cache tests start the real egress proxy and Valkey with the `docker` CLI and are skipped
  without Docker. Set `WEBLENS_REQUIRE_EGRESS_PROXY=1 WEBLENS_REQUIRE_VALKEY=1` to make them fail instead, as CI does.
- Live SearXNG tests run only with `WEBLENS_SEARXNG_URL` set.
- The extraction corpus in `tests/PinkRooster.WebLens.Fetch.Tests/Corpus/` is hand-written pages, not copies of
  real sites.

Warnings are errors, and package versions are pinned centrally in `Directory.Packages.props`.

## License

[MIT](LICENSE). Report security issues as described in [SECURITY.md](SECURITY.md).
