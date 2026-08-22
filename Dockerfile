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

# Non-root. This process holds brokerage credentials and reaches the public internet; there is no reason for
# it to be able to write to its own image.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin mcp
USER 64198

COPY --from=build --chown=64198:64198 /app .

# The ICU data behind TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"). Every session rule in this
# codebase is Central wall-clock, so invariant globalization would break gap detection rather than merely
# formatting -- and it would do so by reporting plausible-looking wrong buckets.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080
ENTRYPOINT ["dotnet", "MarqSpec.Mcp.TopstepX.dll"]
