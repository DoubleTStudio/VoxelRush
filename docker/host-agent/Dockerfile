# VoxelRush host agent (ALLOC-001). Build context: the publish output (tools/build-host-agent.sh).
#   tools/build-host-agent.sh && docker build -f docker/host-agent/Dockerfile -t voxelrush-host-agent:dev build/host-agent
# Configuration comes from environment variables (HostAgent__ServiceToken, HostAgent__ServerImage, ...).
# Static docker CLI only; the daemon is the host's, reached through the mounted socket.
FROM docker:28.5.1-cli@sha256:9190b0613792e658a7783cf14b2d5ace5941bb68ede7276922ea36ee457d76ad AS cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac

COPY --from=cli --chown=root:root --chmod=0755 /usr/local/bin/docker /usr/local/bin/docker
WORKDIR /app
# Root-owned and read-only for the runtime user.
COPY --chown=root:root --chmod=0755 . .

# The CLI keeps its config under $HOME; keep that on the tmpfs.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/tmp \
    DOCKER_CONFIG=/tmp/.docker
# The base image's unprivileged user; socket access comes from group_add, not root.
USER app
EXPOSE 8080
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "VoxelRush.HostAgent.dll"]
