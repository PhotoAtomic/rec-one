# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/DiaryApp.sln ./
COPY src/DiaryApp.Client/DiaryApp.Client.csproj DiaryApp.Client/
COPY src/DiaryApp.Server/DiaryApp.Server.csproj DiaryApp.Server/
COPY src/DiaryApp.Shared/DiaryApp.Shared.csproj DiaryApp.Shared/
RUN dotnet restore DiaryApp.Server/DiaryApp.Server.csproj

COPY src/ ./
RUN dotnet publish DiaryApp.Server/DiaryApp.Server.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

VOLUME ["/data/entries"]

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "DiaryApp.Server.dll"]
