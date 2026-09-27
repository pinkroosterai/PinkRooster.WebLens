# syntax=docker/dockerfile:1
# WebLens: the API host with the search and fetch modules. Built by GitHub Actions, never on the host.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/PinkRooster.WebLens.Api/PinkRooster.WebLens.Api.csproj -c Release -o /app -p:UseAppHost=false

# The Playwright .NET image of the pinned Microsoft.Playwright version: the browser build is coupled to the library,
# and it already carries the ASP.NET Core 10 runtime. Chromium is installed here, at build time; nothing downloads at run time.
FROM mcr.microsoft.com/playwright/dotnet:v1.63.0-noble
COPY --from=build /app /app

# Published files keep the build's modes (rwxr--r-- for Playwright's node driver): the unprivileged user must read
# everything and execute the driver.
RUN chmod -R a+rX /app

# The build fails when the pinned package expects a Chromium revision this base image does not have.
RUN revision=$(grep -A3 '"name": "chromium"' /app/.playwright/package/browsers.json | grep -o '"revision": "[0-9]*"' | grep -o '[0-9]*') \
 && test -n "$revision" && test -d "/ms-playwright/chromium-$revision" \
 || { echo "Microsoft.Playwright wants Chromium revision ${revision:-?}, which this base image does not have"; exit 1; }

WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DOTNET_gcServer=0
EXPOSE 8080

# Non-root (SSRF layer L4, process hardening). pwuser is the base image's unprivileged user.
USER pwuser
ENTRYPOINT ["dotnet", "PinkRooster.WebLens.Api.dll"]
