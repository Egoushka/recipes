# syntax=docker/dockerfile:1
# JIT build on the ASP.NET chiseled image (no shell, non-root). Native AOT is not used in v0.1: see
# docs/specs/2026-11-01-journal-v0.1.md, decision 3.

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-noble
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY Directory.Build.props recipes.slnx ./
COPY src/Recipes/Recipes.csproj src/Recipes/
RUN dotnet restore src/Recipes/Recipes.csproj
COPY src/Recipes/ src/Recipes/
RUN dotnet publish src/Recipes/Recipes.csproj -c Release --no-restore -o /app/publish

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
# /data holds recipes.db; mount a volume owned by uid 1654 (the image's non-root user).
ENV ASPNETCORE_URLS=http://+:8080 \
    Journal__Path=/data/recipes.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "Recipes.dll"]
