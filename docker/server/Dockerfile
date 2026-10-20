# VoxelRush Linux dedicated server. Build context: the server build output (tools/build.sh server).
#   docker build -f docker/server/Dockerfile -t voxelrush-server:dev build/server
#   docker run --rm --init -p 7777:7777/udp voxelrush-server:dev
# The Unity Linux server payload is x86-64 only.
# hadolint ignore=DL3029
FROM --platform=linux/amd64 ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac

# No extra packages yet; add pinned ca-certificates when the server makes HTTPS calls (VR-080).
RUN useradd --system --uid 10001 --home-dir /srv/voxelrush --shell /usr/sbin/nologin voxelrush

WORKDIR /srv/voxelrush
# Root-owned and read-only for the runtime user: the server writes nothing into its install dir.
COPY --chown=root:root --chmod=0755 . .

# Unity writes its config/cache under $HOME; keep that off the read-only install dir.
ENV HOME=/tmp
USER 10001
EXPOSE 7777/udp
STOPSIGNAL SIGTERM

ENTRYPOINT ["./VoxelRushServer.x86_64", "-batchmode", "-nographics", "-logFile", "-"]
CMD ["-port", "7777", "-maxPlayers", "8"]
