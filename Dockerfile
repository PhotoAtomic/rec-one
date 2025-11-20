# Build stage (multi-platform aware) using the .NET 10 SDK with AOT tooling
# Using --platform=$BUILDPLATFORM follows the official multi-arch guidance.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG BUILDPLATFORM
ARG TARGETOS
ARG TARGETARCH
ENV DOTNET_SKIP_WORKLOAD_MANIFEST_UPDATE=1

# Install native toolchain and workloads needed for AOT and Blazor
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential clang zlib1g-dev python3 python-is-python3 \
    # Cross-linker/toolchain for arm64 Native AOT when building on non-arm hosts
    && if [ "$TARGETARCH" = "arm64" ]; then \
        apt-get install -y --no-install-recommends g++-aarch64-linux-gnu binutils-aarch64-linux-gnu; \
       fi \
    && dotnet workload install wasm-tools --skip-manifest-update

WORKDIR /src

COPY src/DiaryApp.sln ./
COPY src/DiaryApp.Client/DiaryApp.Client.csproj DiaryApp.Client/
COPY src/DiaryApp.Server/DiaryApp.Server.csproj DiaryApp.Server/
COPY src/DiaryApp.Shared/DiaryApp.Shared.csproj DiaryApp.Shared/
RUN dotnet restore DiaryApp.Server/DiaryApp.Server.csproj

COPY src/ ./
RUN if [ "$TARGETARCH" = "amd64" ]; then \
      dotnet publish DiaryApp.Server/DiaryApp.Server.csproj \
        -c Release \
        -p:PublishProfile=LinuxAot \
        -o /app/publish; \
    elif [ "$TARGETARCH" = "arm64" ]; then \
      export CC=aarch64-linux-gnu-gcc CXX=aarch64-linux-gnu-g++ OBJCOPY=aarch64-linux-gnu-objcopy STRIP=aarch64-linux-gnu-strip AR=aarch64-linux-gnu-ar RANLIB=aarch64-linux-gnu-ranlib; \
      dotnet publish DiaryApp.Server/DiaryApp.Server.csproj \
        -c Release \
        -p:PublishProfile=LinuxAotArm64 \
        -p:StripSymbols=false \
        -p:ObjCopyPath=/usr/bin/aarch64-linux-gnu-objcopy \
        -o /app/publish; \
    else \
      echo "Unsupported TARGETARCH: $TARGETARCH" && exit 1; \
    fi

# Minimal runtime stage for the native binary
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
ARG TARGETPLATFORM

# Run as root to ensure write access to mounted volumes such as /data/entries
USER 0
WORKDIR /app

# Install minimal native dependencies required by Azure Cognitive Services Speech SDK and FFmpeg
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libuuid1 \
        ca-certificates \
        libcurl4 \
        libssl3 \
        ffmpeg

ENV ASPNETCORE_URLS=http://+:8080
ENV Transcription__Settings__FFmpegPath=/usr/bin
EXPOSE 8080

VOLUME ["/data/entries"]

COPY --from=build /app/publish ./
ENTRYPOINT ["/app/DiaryApp.Server"]
