# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy solution and project files, then restore
COPY CampaignVault.slnx .
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore

# Copy everything else and build
COPY . .

# Publish with trimming enabled to reduce image size
RUN dotnet publish src/CampaignVault/CampaignVault.csproj -c Release -o /app \
    -p:PublishTrimmed=true \
    -p:TrimMode=partial \
    -p:DebugType=none \
    -p:DebugSymbols=false

# Final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Install libicu for RavenDB Embedded
RUN apt-get update && apt-get install -y libicu-dev && rm -rf /var/lib/apt/lists/*

# Create data directory for RavenDB persistence
RUN mkdir -p /app/data
ENV CAMPAIGN_DB_PATH=/app/data/campaign.db
ENV MCP_PORT=8080
ENV MCP_BIND_ANY=1

EXPOSE 8080
ENTRYPOINT ["dotnet", "CampaignVault.dll"]
