# The DNS resolver for WebLens (the app needs a resolver for the URL guard's host check, and nothing else
# outbound). It sits on the app's internal network and on an ordinary one, and answers DNS only.
FROM alpine:3.24.2
RUN apk add --no-cache dnsmasq \
 && adduser -D -H -u 10002 resolver
# No hosts file, no pid file (the root filesystem is read-only), no caching of negative answers; forward to public resolvers.
# dnsmasq binds port 53 as root and then drops to the unprivileged user.
ENTRYPOINT ["dnsmasq", "--keep-in-foreground", "--no-hosts", "--no-resolv", "--no-negcache", "--user=resolver", "--pid-file", \
            "--server=1.1.1.1", "--server=9.9.9.9", "--cache-size=1000", "--log-facility=-"]
HEALTHCHECK --interval=30s --timeout=5s CMD nslookup -timeout=3 example.org 127.0.0.1 >/dev/null || exit 1
