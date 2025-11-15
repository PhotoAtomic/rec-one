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

# Minimal runtime stage that executes the native binary
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-bookworm-slim AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

VOLUME ["/data/entries"]

COPY --from=build /app/publish ./
ENTRYPOINT ["/app/DiaryApp.Server"]
