# Security policy

Please report vulnerabilities privately through GitHub's
[private vulnerability reporting](https://github.com/pinkroosterai/PinkRooster.WebLens/security/advisories/new),
not in a public issue.

Include what you did, what happened, and the version (image tag or commit).

## In scope

- Any way to make WebLens reach an address its SSRF defence should refuse: loopback, private, link-local or cloud
  metadata addresses, through redirects, DNS tricks, alternative IP notations or the browser.
- Authentication or scope bypasses, and rate-limit bypasses for a key.
- Secrets (API keys, the Valkey password, proxy credentials) leaking into responses, logs or the browser process.
- Fetched content escaping into anything other than the Markdown returned to the caller.

## Out of scope

- Deployments that change the documented network layout (the app must have no route out except the egress proxy), or
  that add `--allow-range`, `--allow-address` or `--unsafe-allow-private-ranges` to the proxy.
- The Development configuration, which deliberately allows private networks and runs without the proxy.
- WebLens refusing a site that blocks automated clients. It is designed to report that, not to get around it.
