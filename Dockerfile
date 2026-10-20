FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Spindle.Domain/Spindle.Domain.csproj src/Spindle.Domain/
COPY src/Spindle.Infrastructure/Spindle.Infrastructure.csproj src/Spindle.Infrastructure/
COPY src/Spindle.Api/Spindle.Api.csproj src/Spindle.Api/
RUN dotnet restore src/Spindle.Api
COPY src/Spindle.Domain/ src/Spindle.Domain/
COPY src/Spindle.Infrastructure/ src/Spindle.Infrastructure/
COPY src/Spindle.Api/ src/Spindle.Api/
RUN dotnet publish src/Spindle.Api -c Release --no-restore -o /out \
    -p:SkipWebApp=true -p:OpenApiGenerateDocuments=false -p:DebugType=none
COPY --from=web /web/dist/ /out/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /app/downloads && chown "$APP_UID" /app/downloads
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Spindle.Api.dll"]
