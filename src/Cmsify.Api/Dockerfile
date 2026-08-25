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

COPY ["Directory.Packages.props", "./"]
COPY ["src/Cmsify.Api/Cmsify.Api.csproj", "src/Cmsify.Api/"]
COPY ["src/Cmsify.Contracts/Cmsify.Contracts.csproj", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
COPY ["src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj", "src/Cmsify.Infrastructure/"]
RUN dotnet restore "src/Cmsify.Api/Cmsify.Api.csproj"
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
