# Video Diary (rec-one)

A self-hosted, mobile-ready progressive web application for capturing and searching personal video diary entries. The solution uses Blazor WebAssembly (hosted) with Ahead-of-Time (AOT) compilation and is container-ready for Linux deployments.

## Project structure

```
rec-one/
├── Dockerfile                   # Multi-stage build for containerized hosting
└── src/
    ├── DiaryApp.Client/         # Blazor WebAssembly front-end
    ├── DiaryApp.Server/         # ASP.NET Core host and API surface
    ├── DiaryApp.Shared/         # Shared contracts and models
    └── DiaryApp.sln             # Solution file
```

### Client highlights
- Browser/phone friendly recorder powered by MediaRecorder via JavaScript interop
- Form-based metadata capture with optional automatic transcription, summarization and title generation triggers
- Entry listing with keyword search and transcript peek support
- Installable PWA with offline caching hooks and AOT enabled for smaller footprint and faster startup

### Server highlights
- RESTful endpoints for creating, updating and retrieving diary entries and derived assets
- File-system backed storage with configurable root directory and naming convention
- Pluggable processing services to integrate transcription, summarization and auto-title providers (remote API or sidecar containers)
- In-memory search index placeholder with keyword support and extension point for vector search implementations
- Optional OpenID Connect authentication wiring via `Authentication:OIDC` settings

## Getting started

### Prerequisites
- .NET 8 SDK for local development (AOT publishing requires the full SDK toolchain)
- Node is *not* required; assets are handled by the Blazor build pipeline

### Restore & build

```bash
cd src
dotnet restore
dotnet build
```

To publish with AOT optimizations:

```bash
dotnet publish DiaryApp.Server/DiaryApp.Server.csproj -c Release -o ../publish
```

### Run locally

```
dotnet run --project DiaryApp.Server/DiaryApp.Server.csproj
```

Access the app at `https://localhost:5001` (or `http://localhost:5000`).

### Configuration

Key settings live in `DiaryApp.Server/appsettings.json`:

- `Storage`: configure the root directory (mapped to a container volume) and filename format (`yyyy-MMM-dd HH-mm-ss - {title}` by default).
- `Transcription`, `Summaries`, `Titles`: toggle optional automation features and specify provider identifiers or connection details.
- `Authentication:OIDC`: when populated with `Authority`, `ClientId`, and optional `ClientSecret`, the server enables OpenID Connect authentication with cookie persistence.

### Container deployment

Build and run the container image:

```bash
docker build -t video-diary .
docker run -p 8080:8080 -v $(pwd)/data:/data/entries video-diary
```

Mount the `/data/entries` volume to persist recordings and metadata.

## Next steps

- Replace placeholder transcription/summarization/title services with real integrations (REST, gRPC, or local inference containers)
- Swap the in-memory search index for a persistent store with vector search capabilities (e.g., Qdrant, Pinecone, Azure AI Search)
- Harden OpenID Connect flows with scopes, claims mapping, and role-based authorization
- Add UI for editing entries and managing tags beyond the initial creation flow
- Expand PWA offline support by enriching the service worker asset manifest
