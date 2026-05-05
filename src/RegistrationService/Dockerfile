# ============================================================
# REGISTRATION SERVICE — Dockerfile
# ============================================================
# Multi-stage build to keep the final image small.
# Build context MUST be the solution root (not this directory)
# because we need to COPY the Contracts project.
#
# Build from solution root:
#   docker build -f src/RegistrationService/Dockerfile -t registration-service .
#
# Or via docker-compose (which sets context: . automatically).
# ============================================================

# ------------------------------------------------------------------
# Stage 1: BUILD
# ------------------------------------------------------------------
# Use the full .NET SDK image for building. This image is large (~800MB)
# but is only used during build — it is NOT included in the final image.
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# ------------------------------------------------------------------
# LAYER CACHING OPTIMISATION
# ------------------------------------------------------------------
# Copy .csproj files first and restore dependencies BEFORE copying source.
# Docker caches layers; if .csproj files haven't changed, the restore
# layer is reused on subsequent builds — saving significant time.
#
# We must copy BOTH the Contracts and RegistrationService .csproj files
# because RegistrationService has a ProjectReference to Contracts.
# ------------------------------------------------------------------
COPY src/Contracts/MassTransitPlayground.Contracts.csproj                           src/Contracts/
COPY src/RegistrationService/MassTransitPlayground.RegistrationService.csproj       src/RegistrationService/

RUN dotnet restore src/RegistrationService/MassTransitPlayground.RegistrationService.csproj

# Copy all source files for both projects, then publish
COPY src/Contracts/       src/Contracts/
COPY src/RegistrationService/ src/RegistrationService/

RUN dotnet publish src/RegistrationService/MassTransitPlayground.RegistrationService.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# ------------------------------------------------------------------
# Stage 2: RUNTIME
# ------------------------------------------------------------------
# Use the slim ASP.NET runtime image (~200MB). Does not include the SDK,
# compiler, or build tools — only what's needed to RUN the application.
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy only the published output from the build stage
COPY --from=build /app/publish .

# ASP.NET Core listens on port 8080 by default in .NET 8 containers.
# The docker-compose maps this to a host port.
EXPOSE 8080

ENTRYPOINT ["dotnet", "MassTransitPlayground.RegistrationService.dll"]
