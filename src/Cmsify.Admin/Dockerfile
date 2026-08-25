FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_INFORMATIONAL_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown
COPY ["Directory.Packages.props", "./"]
COPY ["src/Cmsify.Admin/Cmsify.Admin.csproj", "src/Cmsify.Admin/"]
COPY ["src/Cmsify.Contracts/Cmsify.Contracts.csproj", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
COPY ["sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj", "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/"]
RUN dotnet restore "src/Cmsify.Admin/Cmsify.Admin.csproj"
COPY . .
RUN dotnet publish "src/Cmsify.Admin/Cmsify.Admin.csproj" -c Release -o /app/publish --no-restore \
    -p:Version="${BUILD_VERSION}" \
    -p:InformationalVersion="${BUILD_INFORMATIONAL_VERSION}"
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js
FROM base AS final
WORKDIR /app
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="Cmsify Admin" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${BUILD_SOURCE_REVISION}" \
      org.opencontainers.image.source="https://github.com/Syntax-Circus/cmsify" \
      org.opencontainers.image.licenses="AGPL-3.0-or-later"
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Cmsify.Admin.dll"]
