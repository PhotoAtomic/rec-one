# Build stage based on the .NET SDK image
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build

# Install native toolchain and workloads needed for AOT and Blazor
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential clang zlib1g-dev python3 python-is-python3 \
    && dotnet workload install wasm-tools --skip-manifest-update \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY src/DiaryApp.sln ./
COPY src/DiaryApp.Client/DiaryApp.Client.csproj DiaryApp.Client/
COPY src/DiaryApp.Server/DiaryApp.Server.csproj DiaryApp.Server/
COPY src/DiaryApp.Shared/DiaryApp.Shared.csproj DiaryApp.Shared/
RUN dotnet restore DiaryApp.Server/DiaryApp.Server.csproj

COPY src/ ./
RUN dotnet publish DiaryApp.Server/DiaryApp.Server.csproj \
    -c Release \
    -p:PublishProfile=LinuxAot \
    -o /app/publish

# Stage that fetches a minimal static ffmpeg build
FROM debian:bookworm-slim AS ffmpeg
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget ca-certificates xz-utils \
    && wget https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz -O /tmp/ffmpeg.tar.xz \
    && mkdir /ffmpeg \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /ffmpeg --strip-components=1 \
    && rm -rf /var/lib/apt/lists/* /tmp/ffmpeg.tar.xz

# Minimal runtime stage that executes the native binary
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-noble-chiseled AS runtime

# Run as root to ensure write access to mounted volumes such as /data/entries
USER 0
WORKDIR /app

# Provide ffmpeg in a fixed location used by the app configuration (/usr/bin/ffmpeg)
COPY --from=ffmpeg /ffmpeg/ffmpeg /usr/bin/ffmpeg

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

VOLUME ["/data/entries"]

COPY --from=build /app/publish ./
ENTRYPOINT ["/app/DiaryApp.Server"]
