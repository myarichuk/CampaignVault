# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /source

# Copy csproj and restore
COPY *.csproj .
RUN dotnet restore

# Copy everything else and build
COPY . .
RUN dotnet publish -c Release -o /app

# Final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .

# Create data directory for LiteDB persistence
RUN mkdir -p /app/data
ENV CAMPAIGN_DB_PATH=/app/data/campaign.db

EXPOSE 8080
ENTRYPOINT ["dotnet", "CampaignVault.dll", "--urls", "http://0.0.0.0:8080"]
