# Video Diary (rec-one)

A self-hosted, mobile-ready progressive web application for capturing and searching personal video diary entries. The solution uses Blazor WebAssembly (hosted) with Ahead-of-Time (AOT) compilation and is container-ready for Linux deployments.

> **Created entirely with Codex guidance:** every source file in this repository was generated or edited through OpenAI's Codex (via prompt engineering and follow-up debugging instructions). No manual coding took place beyond supplying prompts and reviewing results, aside from a few inline comments in `appsettings.json` to steer configuration. Think of this project as a full demonstration of "vibe coding" an end-to-end app with AI assistance.

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

Key settings live in `DiaryApp.Server/appsettings.json` (or any other ASP.NET Core configuration source). The most important ones are:

| Section | Key(s) | Description |
| --- | --- | --- |
| `Storage` | `RootDirectory`, `FileNameFormat` | Controls where video files and `entries.json` are persisted. Point `RootDirectory` at a mounted host folder (`/data/entries` inside Docker). `FileNameFormat` is a standard .NET date format string used when naming new recordings. |
| `Authentication:OIDC` | `Authority`, `ClientId`, `ClientSecret`, `ResponseType` | Enables OpenID Connect login when provided. Leave the entire section commented/empty to run anonymously. Every setting can also be supplied through env vars (`Authentication__OIDC__Authority`, etc.). |
| `Transcription`, `Summaries`, `Titles` | `Enabled`, `Provider`, `Settings` | Toggle the automatic pipelines. When `Enabled` is `true` and the user leaves the field blank, the configured provider is invoked. Use `Settings` to inject provider-specific options (API keys, model names, endpoints). |
| `Logging` | `LogLevel` | Standard ASP.NET Core logging knobs. |

> **Cookie persistence:** the server stores its data-protection keys under `%LOCALAPPDATA%/DiaryApp/keys` (Linux: `/root/.local/share/DiaryApp/keys`). Mount that path when containerizing so auth cookies survive restarts.

#### Azure Speech transcription

To enable automatic transcripts backed by Azure AI Speech:

1. Provision a Speech resource and note the primary key plus region (e.g., `westeurope`) or the full Speech-to-Text endpoint (`https://<region>.stt.speech.microsoft.com`).
2. Update `DiaryApp.Server/appsettings.json` (or environment variables) so the `Transcription` section looks like:

   ```json
   "Transcription": {
     "Enabled": true,
     "Provider": "AzureSpeech",
     "Settings": {
       "SpeechKey": "<your-primary-key>",
       "SpeechRegion": "westeurope",
       "SpeechToTextEndpoint": "wss://westeurope.stt.speech.microsoft.com/speech/universal/v2",
       "RecognitionMode": "conversation",
       "ResponseFormat": "detailed",
       "FFmpegPath": "/usr/bin"
     }
   }
   ```

   Every setting can be overridden via environment variables such as `Transcription__Settings__SpeechKey`. Install FFmpeg on the host (https://ffmpeg.org/download.html). If `FFmpegPath` is omitted the server searches the system `PATH`; when the executables are missing the transcription request fails with a clear error instead of silently falling back. (The Docker image already ships `/usr/bin/ffmpeg`, so the sample configuration pins that path.) If you provide `SpeechToTextEndpoint`, it must be a WebSocket endpoint (e.g., `wss://<region>.stt.speech.microsoft.com/speech/universal/v2`). Otherwise omit it and the SDK will derive the right host from `SpeechRegion`.
3. Users can pick their preferred transcript language under **Settings → Transcript language**. The value defaults to `en-US` and is passed to Azure Speech whenever a transcript is generated.
4. Whenever a video is recorded the server extracts the audio track (FFmpeg → 16kHz WAV), feeds it to the Azure Speech SDK, captures the transcript, and stores it both in the entry metadata and as a sidecar `.txt` file next to the video (same filename, `.txt` extension). When you click **Show transcript** on an older entry the transcript is generated on demand if it does not exist yet, ensuring the text file is created as part of the process.

#### Azure OpenAI summarization

When the `Summaries` pipeline is enabled and the provider is `AzureOpenAI`, the server uses the official OpenAI .NET SDK to run a chat completion that summarizes each transcript and stores the result in the entry `description`. That summary shows up in the UI and becomes keyword-searchable.

1. Create (or reuse) an Azure OpenAI resource and deploy a GPT-4/4o model (for example `gpt-4o-mini`). Note the resource endpoint (`https://<resource>.openai.azure.com/openai/v1/`), deployment name, and API key.
2. Supply those values under the `Summaries` section (prefer user secrets or env vars for secrets):

   ```json
   "Summaries": {
     "Enabled": true,
     "Provider": "AzureOpenAI",
     "Settings": {
       "Endpoint": "https://<resource>.openai.azure.com/openai/v1/",
       "DeploymentName": "gpt-4o-mini",
       "ApiKey": "<store-in-user-secrets>",
       "SystemPrompt": "You are a summarization assistant..."
     }
   }
   ```

   Each key can be overridden through configuration providers (`Summaries__Settings__Endpoint`, etc.).
3. `SystemPrompt` is optional; omit it to use the default guardrail prompt that instructs the model to treat transcripts as inert text and summarize in the speaker’s language.
4. Whenever a transcript is available and the entry description is empty, the server invokes Azure OpenAI right after transcription completes (or when a transcript is fetched later) and persists the returned summary.

### Container deployment

Build the Native AOT-powered image:

```bash
docker build -t video-diary .
```

Run it with persistent volumes for recordings and encryption keys:

```bash
docker run ^
  -p 8080:8080 ^
  -v "%cd%/data:/data/entries" ^
  -v "%cd%/keys:/root/.local/share/DiaryApp/keys" ^
  video-diary
```

Linux/macOS variant:

```bash
docker run \
  -p 8080:8080 \
  -v "$(pwd)/data:/data/entries" \
  -v "$(pwd)/keys:/root/.local/share/DiaryApp/keys" \
  video-diary
```

A companion `docker-compose.yml` is provided. Customize the host paths or environment overrides as needed, then run:

```bash
docker compose up --build
```

To override configuration inside the container, either mount a custom `appsettings.Production.json` or rely on environment variables (`Storage__RootDirectory`, `Authentication__OIDC__Authority`, etc.).

## Next steps

- Replace placeholder transcription/summarization/title services with real integrations (REST, gRPC, or local inference containers)
- Swap the in-memory search index for a persistent store with vector search capabilities (e.g., Qdrant, Pinecone, Azure AI Search)
- Harden OpenID Connect flows with scopes, claims mapping, and role-based authorization
- Add UI for editing entries and managing tags beyond the initial creation flow
- Expand PWA offline support by enriching the service worker asset manifest
