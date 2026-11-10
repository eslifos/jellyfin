# Stage 1: Web client (pre-compilado localmente)
FROM scratch AS web-builder
COPY jellyfin-web/dist /dist

# Stage 2: Build jellyfin server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-builder
WORKDIR /src
COPY jellyfin/ ./
RUN dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
    --configuration Release \
    --output /app/server \
    --runtime linux-x64 \
    --self-contained false \
    -p:DebugType=none \
    -p:DebugSymbols=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0

ENV JELLYFIN_DATA_DIR=/config \
    JELLYFIN_CACHE_DIR=/cache \
    JELLYFIN_LOG_DIR=/config/log \
    JELLYFIN_WEB_DIR=/opt/jellyfin/web \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    mkvtoolnix \
    libfontconfig1 \
    libfreetype6 \
    libicu-dev \
    ca-certificates \
    curl \
    unzip \
    && rm -rf /var/lib/apt/lists/*

# Tráilers: yt-dlp los descarga para reproducirlos con el reproductor de Jellyfin.
# Deno es el motor de JavaScript que yt-dlp necesita para YouTube.
RUN curl -fsSL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp \
    && chmod 755 /usr/local/bin/yt-dlp \
    && curl -fsSL https://github.com/denoland/deno/releases/latest/download/deno-x86_64-unknown-linux-gnu.zip -o /tmp/deno.zip \
    && unzip -q /tmp/deno.zip -d /usr/local/bin \
    && chmod 755 /usr/local/bin/deno \
    && rm /tmp/deno.zip

COPY --from=server-builder /app/server /opt/jellyfin/server
COPY --from=web-builder /dist /opt/jellyfin/web

RUN useradd -r -s /bin/false -d /config jellyfin \
    && mkdir -p /config /cache /media \
    && chown -R jellyfin:jellyfin /config /cache /media /opt/jellyfin /usr/local/bin/yt-dlp

USER jellyfin

VOLUME ["/config", "/cache", "/media"]
EXPOSE 8096 8920

ENTRYPOINT ["/opt/jellyfin/server/jellyfin", \
    "--datadir", "/config", \
    "--cachedir", "/cache", \
    "--logdir", "/config/log", \
    "--webdir", "/opt/jellyfin/web"]
