# Build and run the MCP server.
#
# Two stages: the SDK image restores and publishes, the runtime image carries only the output. Shipping the
# SDK would roughly quadruple the image and put a compiler on a host that reaches a brokerage API.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the manifests first, restore, and only then copy the source. Restore is the slow layer and it depends
# only on these files, so an ordinary source edit reuses the cached restore instead of re-downloading NuGet.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY MarqSpec.Mcp.TopstepX/*.csproj                  MarqSpec.Mcp.TopstepX/
COPY MarqSpec.Mcp.TopstepX.Domain/*.csproj           MarqSpec.Mcp.TopstepX.Domain/
COPY MarqSpec.Mcp.TopstepX.Data/*.csproj             MarqSpec.Mcp.TopstepX.Data/
RUN dotnet restore MarqSpec.Mcp.TopstepX/MarqSpec.Mcp.TopstepX.csproj

COPY MarqSpec.Mcp.TopstepX/          MarqSpec.Mcp.TopstepX/
COPY MarqSpec.Mcp.TopstepX.Domain/   MarqSpec.Mcp.TopstepX.Domain/
COPY MarqSpec.Mcp.TopstepX.Data/     MarqSpec.Mcp.TopstepX.Data/

# No --no-restore: the source copy above can introduce a project reference the manifest-only restore did not
# see, and failing at publish with a restore error is clearer than succeeding with a stale graph.
RUN dotnet publish MarqSpec.Mcp.TopstepX/MarqSpec.Mcp.TopstepX.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Npgsql probes for GSSAPI/Kerberos when opening a connection, even for password authentication, and without
# this it prints "Cannot load library libgssapi_krb5.so.2" as the FIRST line in the log. It is harmless -- the
# connection succeeds -- which is precisely the problem: an alarming error above a successful startup sends
# the next person debugging something that is not broken.
RUN apt-get update  && apt-get install -y --no-install-recommends libgssapi-krb5-2  && rm -rf /var/lib/apt/lists/*

# Non-root. This process holds brokerage credentials and reaches the public internet; there is no reason for
# it to be able to write to its own image.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin mcp
USER 64198

COPY --from=build --chown=64198:64198 /app .

# The ICU data behind TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"). Every session rule in this
# codebase is Central wall-clock, so invariant globalization would break gap detection rather than merely
# formatting -- and it would do so by reporting plausible-looking wrong buckets.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# ASPNETCORE_HTTP_PORTS is INHERITED from mcr.microsoft.com/dotnet/aspnet:10.0 as 8080, and is left standing
# deliberately. It is why ConfigureDefaultBinding's ephemeral loopback default never applies in here: an
# address is always named inside the image, so stdio binds a fixed 8080 on every container interface rather
# than 127.0.0.1:0 (gh#446; ADR-0007's 2026-09-03 update).
#
# That is not an exposure -- read off Program.cs, not measured. Nothing is published for a stdio container,
# and MapMcp sits inside the Http branch of Program.cs, so the listener answers no MCP route to anyone; each
# container has its own network namespace besides, so two of them cannot collide the way two host processes
# did in gh#392.
#
# Clearing it here was measured and REJECTED. `ASPNETCORE_HTTP_PORTS=""` does give stdio a loopback ephemeral
# port -- but under Mcp__Transport=Http it drops Kestrel to its own default, http://localhost:5000, which
# inside a container is unreachable from outside whatever `-p` mapping is given. It fixes the transport that
# serves nothing and silently breaks the one that serves everything -- and it would answer gh#444, whether the
# HTTP transport is supported outside compose, as a side effect of a change about something else.
# docker-compose.yml sets ASPNETCORE_HTTP_PORTS: "" explicitly, which is where that override belongs.

# Documentary only -- EXPOSE publishes nothing. 8443 is right for the deployment this image supports:
# docker-compose.yml sets ASPNETCORE_HTTPS_PORTS=8443 and clears ASPNETCORE_HTTP_PORTS above, because the
# composed server has been HTTPS-only since gh#416. Outside compose, with no ASPNETCORE_* override, the
# inherited ASPNETCORE_HTTP_PORTS=8080 named above is what actually binds -- measured, fresh build, `docker
# run -i` with no compose: "Now listening on: http://[::]:8080" -- and EXPOSE still says 8443 for that case
# too. A bare `docker run` is not the deployment this number describes.
EXPOSE 8443
ENTRYPOINT ["dotnet", "MarqSpec.Mcp.TopstepX.dll"]
