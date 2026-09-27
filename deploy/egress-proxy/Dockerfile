# The egress proxy: the SSRF boundary (layer L3). Stripe's Smokescreen, built from a pinned tag and commit.
# It resolves DNS itself, refuses private, loopback, link-local, CGNAT and IPv4-embedding IPv6 addresses, and connects to
# the address it checked, so a DNS answer cannot change between the check and the connection.
FROM golang:1.25-alpine AS build
RUN apk add --no-cache git
ARG SMOKESCREEN_TAG=v0.1.0
ARG SMOKESCREEN_COMMIT=609eb89
RUN git clone --depth 1 --branch "${SMOKESCREEN_TAG}" https://github.com/stripe/smokescreen.git /src \
 && cd /src && test "$(git rev-parse --short=7 HEAD)" = "${SMOKESCREEN_COMMIT}"
WORKDIR /src
RUN CGO_ENABLED=0 go build -trimpath -tags smokescreen_no_prometheus -o /smokescreen .

# Alpine rather than distroless: the estate wants a healthcheck on every container, and Smokescreen serves no health
# endpoint from its command line, so the check needs a shell (nc). Nothing else is added; it runs as an unprivileged user.
FROM alpine:3.24.2
RUN adduser -D -H -u 10001 smokescreen
COPY --from=build /smokescreen /smokescreen
USER smokescreen
EXPOSE 4750
HEALTHCHECK --interval=30s --timeout=5s CMD nc -z 127.0.0.1 4750 || exit 1
ENTRYPOINT ["/smokescreen"]
CMD ["--listen-port", "4750"]
