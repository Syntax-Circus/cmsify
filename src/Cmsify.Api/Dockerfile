FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_INFORMATIONAL_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown
ARG CMSIFY_STORAGE_PACKAGE_SHA256=

COPY ["Directory.Packages.props", "./"]
COPY ["src/Cmsify.Api/Cmsify.Api.csproj", "src/Cmsify.Api/"]
COPY ["src/Cmsify.Contracts/Cmsify.Contracts.csproj", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
COPY ["src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj", "src/Cmsify.Infrastructure/"]
RUN --mount=type=secret,id=cmsify_storage_nupkg,target=/run/secrets/SyntaxCircus.Storage.0.2.0-media-reconciliation.4.nupkg \
    local_storage_package=/run/secrets/SyntaxCircus.Storage.0.2.0-media-reconciliation.4.nupkg; \
    if [ -f "$local_storage_package" ]; then \
      test -n "$CMSIFY_STORAGE_PACKAGE_SHA256"; \
      normalized_storage_sha256="$(printf '%s' "$CMSIFY_STORAGE_PACKAGE_SHA256" | tr '[:upper:]' '[:lower:]')"; \
      printf '%s  %s\n' "$normalized_storage_sha256" "$local_storage_package" | sha256sum --check --strict; \
      dotnet restore "src/Cmsify.Api/Cmsify.Api.csproj" --source /run/secrets --source https://api.nuget.org/v3/index.json; \
    else dotnet restore "src/Cmsify.Api/Cmsify.Api.csproj"; fi
COPY . .
RUN dotnet publish "src/Cmsify.Api/Cmsify.Api.csproj" -c Release -o /app/publish --no-restore \
    -p:Version="${BUILD_VERSION}" \
    -p:InformationalVersion="${BUILD_INFORMATIONAL_VERSION}"

FROM base AS final
WORKDIR /app
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="Cmsify API" \
      org.opencontainers.image.ref.name="syntaxcircus/cmsify-api:${BUILD_VERSION}" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${BUILD_SOURCE_REVISION}" \
      org.opencontainers.image.source="https://github.com/Syntax-Circus/cmsify" \
      org.opencontainers.image.licenses="AGPL-3.0-or-later"
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Cmsify.Api.dll"]
