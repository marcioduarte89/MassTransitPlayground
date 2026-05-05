# ============================================================
# RISK SERVICE — Dockerfile
# ============================================================
# Identical pattern to the RegistrationService Dockerfile.
# Build context must be the solution root.
# ============================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Contracts/MassTransitPlayground.Contracts.csproj       src/Contracts/
COPY src/RiskService/MassTransitPlayground.RiskService.csproj   src/RiskService/

RUN dotnet restore src/RiskService/MassTransitPlayground.RiskService.csproj

COPY src/Contracts/   src/Contracts/
COPY src/RiskService/ src/RiskService/

RUN dotnet publish src/RiskService/MassTransitPlayground.RiskService.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "MassTransitPlayground.RiskService.dll"]
