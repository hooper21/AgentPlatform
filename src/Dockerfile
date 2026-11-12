# syntax=docker/dockerfile:1
# Один Dockerfile на все сервисы solution: проект выбирается build-arg PROJECT.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${DOTNET_SDK_IMAGE} AS build
ARG PROJECT
WORKDIR /source
COPY . .
RUN dotnet publish "${PROJECT}/${PROJECT}.csproj" -c Release -o /app/publish --nologo /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS final
ARG PROJECT
ENV APP_DLL="${PROJECT}.dll"
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
WORKDIR /app
# curl нужен docker healthcheck-ам (GET /health).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
EXPOSE 8080
# Аргументы (например, --migrate --seed) пробрасываются в dotnet через "$@".
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"/app/${APP_DLL}\" \"$@\"", "--"]
