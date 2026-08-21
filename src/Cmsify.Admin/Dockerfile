FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG BUILD_VERSION=0.0.0-local
ARG BUILD_INFORMATIONAL_VERSION=0.0.0-local
ARG DISABLE_GITVERSION_TASK=true
COPY ["src/Cmsify.Admin/Cmsify.Admin.csproj", "src/Cmsify.Admin/"]
COPY ["src/Cmsify.Core/Cmsify.Core.csproj", "src/Cmsify.Core/"]
RUN dotnet restore "src/Cmsify.Admin/Cmsify.Admin.csproj"
COPY . .
RUN dotnet publish "src/Cmsify.Admin/Cmsify.Admin.csproj" -c Release -o /app/publish --no-restore \
    -p:Version="${BUILD_VERSION}" \
    -p:InformationalVersion="${BUILD_INFORMATIONAL_VERSION}" \
    -p:DisableGitVersionTask="${DISABLE_GITVERSION_TASK}"

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Cmsify.Admin.dll"]
