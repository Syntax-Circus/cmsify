FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:011bb5f30180717b1c8b65822ff2c99bcb96bc65af0164589751b83c7b4949f7 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510 AS build
WORKDIR /src
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_INFORMATIONAL_VERSION=0.0.0-local
ARG BUILD_SOURCE_REVISION=unknown
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Build.targets", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/Cmsify.Admin/Cmsify.Admin.csproj", "src/Cmsify.Admin/"]
COPY ["src/Cmsify.Admin/packages.lock.json", "src/Cmsify.Admin/"]
COPY ["src/Cmsify.Components.Theme/Cmsify.Components.Theme.csproj", "src/Cmsify.Components.Theme/"]
COPY ["src/Cmsify.Components.Theme/packages.lock.json", "src/Cmsify.Components.Theme/"]
COPY ["src/Cmsify.Components/Cmsify.Components.csproj", "src/Cmsify.Components/"]
COPY ["src/Cmsify.Components/packages.lock.json", "src/Cmsify.Components/"]
COPY ["src/Cmsify.Contracts/Cmsify.Contracts.csproj", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Contracts/packages.lock.json", "src/Cmsify.Contracts/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
COPY ["src/Cmsify.Core/packages.lock.json", "src/Cmsify.Core/"]
COPY ["sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj", "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/"]
COPY ["sdk/dotnet/src/SyntaxCircus.Cmsify.Client/packages.lock.json", "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/"]
RUN dotnet restore "src/Cmsify.Admin/Cmsify.Admin.csproj" --locked-mode
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
