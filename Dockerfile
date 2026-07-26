FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy project files
COPY src/XcavateProfileApi/XcavateProfileApi.csproj src/XcavateProfileApi/
COPY src/XcavateProfileApiClient/XcavateProfileApiClient.csproj src/XcavateProfileApiClient/
COPY src/XcavateBuckets.Domain/XcavateBuckets.Domain.csproj src/XcavateBuckets.Domain/
COPY tests/XcavateProfile.ApiTests/XcavateProfile.ApiTests.csproj tests/XcavateProfile.ApiTests/

# Restore dependencies
RUN dotnet restore src/XcavateProfileApi/XcavateProfileApi.csproj

# Copy remaining source code
COPY src/ src/
COPY tests/ tests/
# StrawberryShake codegen reads this schema via src/XcavateProfileApiClient/Buckets/.graphqlrc.json
COPY docs/graphql/ docs/graphql/

# Build the project
RUN dotnet build src/XcavateProfileApi/XcavateProfileApi.csproj -c Release --no-restore

# Publish the application
FROM build AS publish
WORKDIR /app
RUN dotnet publish src/XcavateProfileApi/XcavateProfileApi.csproj -c Release -o /app/publish

# Final stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# libgssapi-krb5-2: required by Npgsql auth negotiation (removed from aspnet images in .NET 8+)
# curl: required by the HEALTHCHECK below (not included in aspnet images)
RUN apt-get update && \
    apt-get install -y --no-install-recommends libgssapi-krb5-2 curl && \
    rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN useradd -m -s /bin/bash appuser

# Copy published files
COPY --from=publish /app/publish .

# Set permissions
RUN chown -R appuser:appuser /app && \
    chmod -R 755 /app

# Switch to non-root user
USER appuser

# Expose port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5000/health 2>/dev/null || exit 1

# Entrypoint
ENTRYPOINT ["dotnet", "XcavateProfileApi.dll"]
