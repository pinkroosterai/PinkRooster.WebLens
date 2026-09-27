# Egress proxy

The browser's only way out (SSRF layer L3). Every browser context is bound to it (`WebLens:Fetch:Security:EgressProxy:Server`), and the host refuses to start without it outside Development and Testing.

Build and run:

```bash
docker build -t weblens-egress-proxy deploy/egress-proxy
docker run --rm -p 127.0.0.1:4750:4750 weblens-egress-proxy \
  --listen-port 4750 \
  --deny-range 172.20.0.0/16          # the network SearXNG and Valkey live on, when it is not already private
```

What it refuses by default: loopback, private (RFC 1918, `fc00::/7`), link-local (including `169.254.169.254`), unspecified, CGNAT `100.64.0.0/10`, IPv6 forms that embed IPv4 (NAT64, 6to4, Teredo, IPv4-mapped) and connections back to itself. Add `--deny-range` for any internal range that is not private, such as a public-looking SearXNG address, and this service's own address when it is not loopback.

Never add `--allow-range`, `--allow-address` or `--unsafe-allow-private-ranges` in a deployment: they punch holes in the boundary. The browser tests use `--allow-address` for their loopback fixture site only.

A refused request is a `407` with an `X-Smokescreen-Error` header (plain http) or `net::ERR_PROXY_AUTH_UNSUPPORTED` in the browser (https). WebLens reports both to callers as `502 target-unavailable` and logs them at Warning as a security signal.

The deployment's network must give the fetch process no other route out: put the app on internal networks only, with this proxy and the DNS resolver as the only containers that reach the internet (see `compose.example.yml`).
