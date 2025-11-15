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
- Settings page lets each user pick preferred devices, transcript locale, and a favorite tag list that guides AI auto-tagging
- Entry listing with keyword search and transcript peek support
- Installable PWA with offline caching hooks and AOT enabled for smaller footprint and faster startup

### Server highlights
- RESTful endpoints for creating, updating and retrieving diary entries and derived assets
- File-system backed storage with configurable root directory and naming convention
- Pluggable processing services to integrate transcription, summarization, auto-title, tag-suggestion providers, and now semantic search embeddings (via Azure OpenAI)
- In-memory search index with keyword search plus optional Azure OpenAI embedding powered semantic search over descriptions
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
| `Transcription`, `Summaries`, `Titles`, `TagSuggestions` | `Enabled`, `Provider`, `Settings` | Toggle the automatic pipelines. When `Enabled` is `true` and the user leaves the field blank, the configured provider is invoked. Use `Settings` to inject provider-specific options (API keys, model names, endpoints). `TagSuggestions` looks at the user's favorite tag list (managed under **Settings**) and appends AI-selected tags when entries are saved or transcripts are requested. |
| `Logging` | `LogLevel` | Standard ASP.NET Core logging knobs. |
| `SemanticSearch` | `Enabled`, `Provider`, `Settings` | Optional Azure OpenAI embedding support for description-based semantic search. When enabled, the server stores vectors in-memory and falls back to keyword search when embeddings cannot be generated. |

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

#### Azure OpenAI title generation

Title generation follows the same pattern. When `Titles.Enabled` is `true` and the provider is `AzureOpenAI`, the server creates concise titles from the summary/description using a configurable system prompt.

```json
"Titles": {
  "Enabled": true,
  "Provider": "AzureOpenAI",
  "Settings": {
    "Endpoint": "https://<resource>.openai.azure.com/openai/v1/",
    "DeploymentName": "gpt-4o-mini",
    "ApiKey": "<store-in-user-secrets>",
    "SystemPrompt": "You are a helpful assistant that writes concise, catchy titles (max 8 words) based on diary entry summaries. Respond with title text only."
  }
}
```

If `SystemPrompt` is omitted, the default prompt above is applied. Titles are only requested when the user did not provide a custom title and a summary/description is available, ensuring autogenerated titles never overwrite explicit user input.

#### Azure OpenAI tag suggestions

When `TagSuggestions.Enabled` is `true`, the backend will ask Azure OpenAI to select tags from the user's favorite list. Suggestions run right after an entry is saved (once a description exists) and whenever someone clicks **Show transcript**, so AI tags can arrive later even if the description was filled in manually.

```json
"TagSuggestions": {
  "Enabled": true,
  "Provider": "AzureOpenAI",
  "Settings": {
    "Endpoint": "https://<resource>.openai.azure.com/openai/v1/",
    "DeploymentName": "gpt-4o-mini",
    "ApiKey": "<store-in-user-secrets>",
    "SystemPrompt": "You are an AI assistant that analyzes a diary video description and selects relevant tags from a provided list. Respond ONLY with JSON shaped as {\"selectedTags\":[\"tag-a\",\"tag-b\"]}. Never invent new tags and return an empty array when nothing matches."
  }
}
```

Tags suggested by the model are merged with whatever the author typed and deduplicated so no entry ends up with repeated labels.

#### Azure OpenAI semantic search

Semantic search piggybacks on the same Azure OpenAI resource you already use for titles and summaries. Provision an embeddings deployment (for example `text-embedding-3-small`) and supply its endpoint + key under `SemanticSearch`:

```json
"SemanticSearch": {
  "Enabled": true,
  "Provider": "AzureOpenAI",
  "Settings": {
    "Endpoint": "https://<resource>.cognitiveservices.azure.com/openai/v1/",
    "DeploymentName": "text-embedding-3-small",
    "ApiKey": "<store-in-user-secrets>"
  }
}
```

When enabled, every entry description is embedded during indexing and cached in-memory alongside keyword metadata. The `/api/search` endpoint now prefers semantic matches (cosine similarity) and automatically falls back to the traditional keyword flow when embeddings are unavailable or the provider is disabled.

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

### Enabling HTTPS with a local certificate

For camera/microphone access from devices on your network, browsers require a secure context (HTTPS). You can terminate TLS directly in Kestrel by mounting a certificate into the container and configuring endpoints via environment variables.

1. Create a self-signed certificate (example using OpenSSL, adjust CN and paths as needed):

   ```bash
   mkdir certs
   openssl req -x509 -newkey rsa:2048 -nodes \
     -keyout certs/rec-one.key \
     -out certs/rec-one.crt \
     -subj "/CN=rec-one.local" -days 365

   openssl pkcs12 -export \
     -in certs/rec-one.crt \
     -inkey certs/rec-one.key \
     -out certs/rec-one.pfx \
     -name rec-one \
     -password pass:yourpassword
   ```

   Import and trust the resulting certificate on your client devices so browsers accept `https://rec-one.local` without warnings.

2. Mount the PFX in Docker Compose and configure Kestrel (see `docker-compose.yml` in this repo for a ready-to-use example):

   ```yaml
   services:
     diaryapp:
       # build: .        # or use a pre-built image
       container_name: rec-one
       ports:
         - "80:80"
         - "443:443"
       environment:
         ASPNETCORE_ENVIRONMENT: Production
         ASPNETCORE_URLS: ""
         Kestrel__Endpoints__Http__Url: http://+:80
         Kestrel__Endpoints__Https__Url: https://+:443
         Kestrel__Endpoints__Https__Certificate__Path: /https/rec-one.pfx
         Kestrel__Endpoints__Https__Certificate__Password: <pfx-password>
         Storage__RootDirectory: /data/entries
         # ...other settings as needed...
       volumes:
         - ./data:/data/entries
         - ./keys:/root/.local/share/DiaryApp/keys
         - ./certs:/https:ro
   ```

After starting the stack with `docker compose up -d`, you can access the app over HTTPS at your chosen host name (for example `https://rec-one.local/`), and modern browsers will allow webcam access once the certificate is trusted.

### Building a multi-architecture image (x64 + Raspberry Pi 5)

The provided `Dockerfile` is configured to produce Native AOT images for both `linux/amd64` and `linux/arm64` (Raspberry Pi 5) using Docker Buildx. This lets you publish a single image tag that works on standard 64-bit Linux hosts and on a Pi 5.

First, create and bootstrap a buildx builder (one-time setup):

```bash
docker buildx create --name diaryapp-multi --use
docker buildx inspect --bootstrap
```

Then build and push a multi-architecture image (replace `your-registry` as needed, e.g., `photoatomic` for Docker Hub):

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t your-registry/rec-one-diaryapp:latest \
  -t your-registry/rec-one-diaryapp:1.0.0 \
  --push .
```

- On a standard 64-bit Linux machine, `docker pull your-registry/rec-one-diaryapp:latest` will fetch the `linux/amd64` variant.
- On a Raspberry Pi 5, the same tag resolves to `linux/arm64`.

You can then reference this image from `docker-compose.yml` instead of building locally:

```yaml
services:
  diaryapp:
    image: your-registry/rec-one-diaryapp:latest
    # other settings (ports, volumes, environment) unchanged
```

## Next steps

Upgrade to .NET 10
