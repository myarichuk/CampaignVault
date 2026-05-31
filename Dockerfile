# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /source

# Copy solution and project files, then restore
COPY CampaignVault.slnx .
COPY src/CampaignVault/CampaignVault.csproj ./src/CampaignVault/
COPY tests/CampaignVault.Tests/CampaignVault.Tests.csproj ./tests/CampaignVault.Tests/
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish src/CampaignVault/CampaignVault.csproj -c Release -o /app

# Final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .

# Install libicu for RavenDB Embedded
RUN apt-get update && apt-get install -y libicu-dev && rm -rf /var/lib/apt/lists/*

# Create data directory for RavenDB persistence
RUN mkdir -p /app/data
ENV CAMPAIGN_DB_PATH=/app/data/campaign.db

EXPOSE 8080
ENTRYPOINT ["dotnet", "CampaignVault.dll", "--urls", "http://0.0.0.0:8080"]
