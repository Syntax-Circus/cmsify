FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS base
WORKDIR /app
EXPOSE 8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_INFORMATIONAL_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Build.targets", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/Cmsify.Api/Cmsify.Api.csproj", "src/Cmsify.Api/"]
COPY ["src/Cmsify.Api/packages.lock.json", "src/Cmsify.Api/"]
COPY ["src/Cmsify.Contracts/Cmsify.Contracts.csproj", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Contracts/packages.lock.json", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
COPY ["src/Cmsify.Core/packages.lock.json", "src/Cmsify.Core/"]
COPY ["src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj", "src/Cmsify.Infrastructure/"]
COPY ["src/Cmsify.Infrastructure/packages.lock.json", "src/Cmsify.Infrastructure/"]
RUN dotnet restore "src/Cmsify.Api/Cmsify.Api.csproj" --locked-mode
COPY . .
RUN dotnet publish "src/Cmsify.Api/Cmsify.Api.csproj" -c Release -o /app/publish --no-restore \
    -p:Version="${BUILD_VERSION}" \
    -p:InformationalVersion="${BUILD_INFORMATIONAL_VERSION}"

FROM base AS final
WORKDIR /app
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="Cmsify API" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${BUILD_SOURCE_REVISION}" \
      org.opencontainers.image.source="https://github.com/Syntax-Circus/cmsify" \
      org.opencontainers.image.licenses="AGPL-3.0-or-later"
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Cmsify.Api.dll"]
