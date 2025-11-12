# Build stage running on top of the ASP.NET runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS build

# Install the .NET SDK so we can publish inside this image
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget ca-certificates apt-transport-https gnupg \
    && wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-8.0 build-essential clang zlib1g-dev python3 python-is-python3 \
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
FROM debian:bookworm-slim AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

VOLUME ["/data/entries"]

COPY --from=build /app/publish ./
ENTRYPOINT ["/app/DiaryApp.Server"]
