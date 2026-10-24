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
# util-linux provides runuser. A Railway volume is mounted as root; the documented start
# command chowns it and then drops to uid 64198. The image entrypoint itself stays dotnet.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 util-linux \
    && rm -rf /var/lib/apt/lists/*

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
# deliberately for the HTTP transport. Clearing it here was measured and REJECTED (ADR-0007, 2026-09-03):
# `ASPNETCORE_HTTP_PORTS=""` under Mcp__Transport=Http drops Kestrel to http://localhost:5000, which
# inside a container is unreachable from outside whatever `-p` mapping is given.
#
# Under stdio the inherited variable is not an operator-named listener. ConfigureDefaultBinding ignores
# it and starts a server that binds no port, so two `docker run --rm -i` containers sharing a network
# namespace no longer collide on 8080 (gh#696). MapMcp stays inside the Http branch. An operator who
# wants a health or metrics listener under stdio names one with ASPNETCORE_URLS or ASPNETCORE_HTTPS_PORTS.
# docker-compose.yml sets ASPNETCORE_HTTP_PORTS: "" explicitly, which is where that override belongs
# for the composed HTTPS path.

# Documentary only -- EXPOSE publishes nothing. 8443 is right for the deployment this image supports:
# docker-compose.yml sets ASPNETCORE_HTTPS_PORTS=8443 and clears ASPNETCORE_HTTP_PORTS above, because the
# composed server has been HTTPS-only since gh#416. A bare `docker run -i` is stdio and binds no HTTP
# port (gh#696). The inherited ASPNETCORE_HTTP_PORTS=8080 still binds under Mcp__Transport=Http with no
# override, which is the HTTP-without-compose recipe, not this EXPOSE. A bare `docker run` is not the
# deployment this number describes.
EXPOSE 8443
ENTRYPOINT ["dotnet", "MarqSpec.Mcp.TopstepX.dll"]
