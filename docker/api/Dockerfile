# VoxelRush metadata API (VR-080). Build context: the publish output (tools/build-api.sh).
#   tools/build-api.sh && docker build -f docker/api/Dockerfile -t voxelrush-api:dev build/api
# Configuration comes from environment variables (VoxelRush__ConnectionString, VoxelRush__JwtSigningKey, ...).
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac

WORKDIR /app
# Root-owned and read-only for the runtime user.
COPY --chown=root:root --chmod=0755 . .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
# The base image's unprivileged user.
USER app
EXPOSE 8080
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "VoxelRush.Api.dll"]
