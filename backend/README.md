# Knowledge Base — backend

ASP.NET Core 10 Web API for the KBMS design (pmtool workspace usa-1, project KBMS). Created with the
official templates (`dotnet new classlib`, `dotnet new webapi`, `dotnet new xunit`, `dotnet new sln`).

```
backend/
  KnowledgeBase.slnx
  src/
    KnowledgeBase.Core/   domain, application services, persistence, storage, workers — host-agnostic
    KnowledgeBase.Api/    minimal-API endpoints, JWT auth, ProblemDetails, OpenAPI
  tests/
    KnowledgeBase.Tests/  xunit (no database needed)
```

## Projects

**KnowledgeBase.Core** holds everything that decides anything:

| Folder | What lives there |
|---|---|
| `Domain/` | Entities: org (college → department → program → faculty, non-academic departments with a head and staff), calendar, document types, requirements, library (folders, documents, immutable versions), access grants, workflow, processing, audit |
| `Application/` | One folder per module (Org, Settings, Requirements, Library, Access, Workflow, Admin). Services enforce authorization themselves, so every host gets the same rules |
| `Application/Abstractions/Contracts.cs` | Cross-module contracts (`IAccessEvaluator`, `IAcademicFolderService`, `IDocumentFactory`, `IRequirementResolver`, `IProcessingPipeline`, `IAuditWriter`, `IOutbox`) |
| `Application/Ai/` | AI contracts (`IEmbeddingProvider`, `IChatModel`, `IVectorIndex`), options, retrieval (`KnowledgeRetriever`) and the assistant (`AssistantService`) |
| `Infrastructure/Ai/` | Text extraction, chunking, BM25 sparse vectors, the Ollama/OpenAI-compatible embeddings and chat clients, Qdrant, Claude, and the search index worker |
| `Infrastructure/Persistence/` | EF Core + PostgreSQL (snake_case, xmin concurrency, partial unique indexes, tsvector search, triggers), seed data |
| `Infrastructure/Storage/` | `IObjectStorage`: S3-compatible storage with presigned URLs, or a local disk store that issues the same kind of signed, expiring URLs |
| `Infrastructure/Processing/` | Processing pipeline worker, outbox dispatcher, housekeeping |

**KnowledgeBase.Api** only translates HTTP: it reads the JWT into an `IIdentitySource`, binds
requests, calls Core services and writes RFC 9457 ProblemDetails.

## Run it

Prerequisites: .NET SDK 10, PostgreSQL 16+ (developed on 18).

```powershell
cd backend
dotnet tool restore

# Once: copy the Development settings (git-ignored) and put your Postgres password in ConnectionStrings:Kbms.
# The Docker image leaves it out (.dockerignore); compose passes the keys as environment variables.
Copy-Item src/KnowledgeBase.Api/appsettings.Development.example.json src/KnowledgeBase.Api/appsettings.Development.json

dotnet run --project src/KnowledgeBase.Api
```

In Development the API applies migrations and seeds the database on startup
(`Database:MigrateOnStartup`, `Database:Seed`). The seed is only the System Administrator (`admin`)
and editable defaults for the academic calendar, retention policies, document types and submission
types; the admin adds colleges, departments, programs and people under Settings. The API listens on
http://localhost:5259 and serves:

- API reference: http://localhost:5259/scalar
- OpenAPI document: http://localhost:5259/openapi/v1.json
- Health: http://localhost:5259/health

### In Docker

`../compose.yaml` runs PostgreSQL, S3 storage ([RustFS](https://rustfs.com); MinIO's community
edition is archived), Qdrant and Ollama for AI search, this API and the web app. Its header lists the commands and ports, and
`../.env.example` lists what can be overridden. Two ways to use it:

- `docker compose up -d --build`: everything, at http://localhost:25480 (nginx serves the SPA and
  proxies `/api`). The API runs in Development, so the dev sign-in and the seed work.
- `docker compose up -d db s3 qdrant ollama ollama-pull`: only the dependencies, with the API on the
  host pointed at them. The host ports are a 254xx block, clear of the usual defaults, and
  `appsettings.Development.json` already points S3 (25490), Qdrant (25463) and Ollama (25434) at them.
  To use the Docker PostgreSQL (25432) instead of a local one:

  ```powershell
  $env:ConnectionStrings__Kbms = "Host=localhost;Port=25432;Database=kbms;Username=kbms;Password=kbms-dev-password"
  dotnet run --project src/KnowledgeBase.Api
  ```

With an NVIDIA GPU, add `-f compose.yaml -f compose.gpu.yaml` to these commands (or set `COMPOSE_FILE`
in `.env`) so Ollama runs the embedding and answer models on it. On CPU only, answers from the
local model take far longer.

### On a server (Dokploy)

Deploy `../compose.yaml` as a Docker Compose service, and paste `../dokploy.env.example`, with its
secrets and the server's LAN address filled in, into the Environment tab. Its header lists the
Dokploy settings. There is no domain: people open `http://<server address>:25480`, and Dokploy's
Traefik isn't involved. Browsers upload and download straight to S3, so the server publishes the site
(25480) and S3 (25490) on the LAN (`KBMS_BIND_ADDRESS=0.0.0.0`), and `KBMS_WEB_URL` and
`KBMS_S3_PUBLIC_URL` carry that address. PostgreSQL and the RustFS console stay on the server
(`KBMS_ADMIN_BIND_ADDRESS=127.0.0.1`). The template also notes the domain-and-HTTPS variant for
later. The server runs Production, so there is no demo sign-in: people sign in with passwords (see
"Signing in" below), starting with `admin` and `KBMS_ADMIN_PASSWORD`.

Opened by an IP address over plain HTTP, the page isn't a secure context, so browser APIs that need
HTTPS (such as `crypto.randomUUID`) are missing; the web app must not depend on them.

### Signing in

There is no university IdP yet, so KBMS checks passwords itself and signs its own JWTs with
`Auth:SigningKey` (`KBMS_SIGNING_KEY`; Production refuses the example key):

```http
POST http://localhost:5259/api/auth/sign-in
Content-Type: application/json

{ "login": "admin", "password": "…", "newPassword": null }
```

Send the returned `accessToken` as `Authorization: Bearer …` (it lasts `Auth:TokenHours`, 8).
`login` is the sign-in id (`users.external_id`, any case) or an email only one active person has.

- **Passwords come from an administrator.** Settings › Users › a person › Set password gives them a
  temporary one (`PUT /api/admin/users/{id}/password`). Their first sign-in answers 422
  `password-change-required`; the same request with `newPassword` sets their own and signs them in.
  The first administrator's comes from `Database:Admin:Password` (`KBMS_ADMIN_PASSWORD`), applied
  while `admin` has none. There is no email reset: an administrator sets a new one.
- **Changing your own** (`POST /api/auth/password`, account menu › Change password) needs the current
  one and returns a new token.
- **Sessions end** when a password is set or changed: `user_credentials.sessions_valid_from` turns
  away older tokens (401). Deactivating someone ends theirs too.
- **Guessing:** five wrong passwords in a row lock the account for 15 minutes (429
  `sign-in-locked`; a new password unlocks it), and each client address gets 10 attempts a minute.
  Hashes are PBKDF2-HMAC-SHA512 (210,000 iterations) in `user_credentials`, apart from `users`.
- **Locked out as the only administrator:** delete their `user_credentials` row and restart; the
  seed applies `Database:Admin:Password` again.

In Development there is also the pick-a-person sign-in: `GET /api/dev/users` lists the active
people and `POST /api/dev/token` (`{ "externalId": "admin" }`) issues a token for one of them, no
password. `GET /api/auth/options` tells the frontend which of the two to offer.

A fresh database has one account, `admin` (System Administrator); the admin adds everyone else under
Settings › Users. The admin manages structure and access but doesn't open documents: add a library
folder, then share it under Manage access so people can upload into it.

The frontend's sign-in page can keep several accounts signed in at once, each tab working as one of
them (see `frontend/README.md`).

With an IdP, set `Auth:Authority` (OIDC issuer) and `Auth:Audience`: the API then trusts its tokens
(their `sub` must match `users.external_id`), and neither password nor dev sign-in is mapped.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:Kbms` | — | Required. Use user-secrets or an environment variable |
| `Auth:Authority`, `Auth:Audience` | empty, `kbms-api` | Empty authority = KBMS signs its own tokens: password sign-in, plus the dev sign-in in Development |
| `Auth:SigningKey`, `Auth:TokenHours` | dev value, `8` | Signs those tokens (32+ characters; `Auth:DevSigningKey` is its earlier name). Production refuses the example key |
| `Database:Admin:Password` | empty | The `admin` account's first password, applied while it has none; changed at the first sign-in |
| `Storage:Provider` | `Local` | `Local` (files on disk, served by the API) or `S3` |
| `Storage:LocalRoot`, `Storage:SigningKey`, `Storage:PublicBaseUrl` | `storage`, dev values | Local store: files under `src/KnowledgeBase.Api/storage` (git-ignored), and the HMAC key and API base URL for its signed URLs |
| `Storage:S3:ServiceUrl`, `Storage:S3:PublicUrl` | empty | The S3 endpoint the API uses, and the one browsers use for presigned URLs. Signatures include the host, so in Docker they differ (`http://s3:9000` vs `http://localhost:25490`). Empty ServiceUrl = AWS S3 in `Storage:S3:Region` |
| `Storage:S3:Bucket`, `AccessKey`, `SecretKey`, `CreateBucket` | `kbms`, empty, empty, `false` | Empty keys use the AWS default credential chain. Upload URLs sign `If-None-Match: *`, so a link can't replace a stored file |
| `Processing:WorkersEnabled` | `true` | Runs the pipeline, outbox and housekeeping workers in the API |
| `Ocr:Enabled` | `true` | OCR of scanned PDF pages and PNG/JPEG uploads with Tesseract; see [OCR](#ocr). Without Tesseract and pdftoppm on the host it reports itself off |
| `Ocr:Languages`, `Dpi`, `MinWordConfidence` | `eng+fil`, 300, 30 | Tesseract language codes (each needs its `tesseract-ocr-<code>` package), the resolution PDF pages are rendered at, and the confidence below which a word is dropped |
| `Ocr:Parallelism`, `PageTimeoutSeconds`, `MaxPages` | 0 (half the cores, at most 4), 120, 200 | Pages read at once, the time one page may take, and the pages read per document |
| `Ocr:TesseractPath`, `PdfToPpmPath` | `tesseract`, `pdftoppm` | The programs, when they aren't on the PATH |
| `Ai:Enabled` | `false` (`true` in Development and Docker) | Chunk/Embed/Index stages, semantic search and the assistant; see [AI](#ai-semantic-search-and-the-assistant) |
| `Ai:Embedding:Endpoint`, `Model`, `Dimensions` | `http://localhost:11434/v1` (Development: 25434, the Docker Ollama), `bge-m3`, `1024` | Any OpenAI-compatible `/embeddings` endpoint (`ApiKey` if it needs one). Changing the model or dimensions starts a new index build |
| `Ai:VectorStore:Url`, `ApiKey`, `CollectionAlias` | `http://localhost:6333` (Development: 25463, the Docker Qdrant), empty, `kb_chunks` | Qdrant |
| `Ai:Chunking:*` | 500 / 800 / 80 / 120 tokens | Target, ceiling, overlap, minimum. Bump `Processing:PipelineVersion` after changing them |
| `Ai:Retrieval:*` | 40 candidates, 8 passages (3 per document), `MinScore` 0.45, `MinPassageScore` 0.40, 20 questions/minute | Below `MinScore` the answer is "not enough evidence" and the model isn't called |
| `Ai:Chat:Provider` | `Local` | Who answers: `Local` (an OpenAI-compatible chat server, Ollama by default) or `Anthropic` (Claude) |
| `Ai:Chat:Local:Endpoint`, `Model`, `ApiKey` | `http://localhost:11434/v1` (Development: 25434, the Docker Ollama), `qwen3.5:4b`, empty | Any OpenAI-compatible `/chat/completions` that streams: Ollama, vLLM, LM Studio, llama.cpp's `llama-server`. With Ollama, the model must be pulled first |
| `Ai:Chat:Local:MaxTokens`, `Temperature`, `ReasoningEffort`, `TimeoutSeconds` | 1500, 0.2, `none`, 300 | `none` turns off a thinking model's reasoning (empty = the server's default). The timeout covers loading the model and reading the passages, up to the first token |
| `Ai:Chat:Anthropic:Model`, `ApiKey`, `MaxTokens`, `Effort` | `claude-opus-5-5`, empty, 16000, `medium` | Empty key = the `ANTHROPIC_API_KEY` environment variable |
| `Ai:Audit:KeepText` | `false` | Keep question and answer text in `ai_query_audits` (ids, scores and outcome are always kept) |
| `Processing:PipelineVersion` | `1` | Keys the extraction and chunk checkpoints; bumping it re-derives and re-indexes every version |
| `Database:MigrateOnStartup`, `Database:Seed` | `false` (`true` in Development) | Apply migrations and seed on startup |
| `Database:Admin:ExternalId`, `DisplayName`, `Email` | `admin`, `System Administrator`, `admin@usa.edu.ph` | The admin the seed creates when no System Administrator is active |
| `Cors:Origins` | `http://localhost:5173` (dev) | The Vite frontend |

## Endpoints

All under `/api` and signed-in unless noted; the full list with schemas is in the API reference.

| Area | Routes |
|---|---|
| Me & org | `GET /me`, `GET /org/tree`, `/org/programs/{id}`, `/org/departments/{id}`, `/org/people`; admin: department head/staff, program supervisor and faculty, college dean |
| Colleges & departments (admin) | `GET /admin/org/structure` (units with what each holds), `POST\|PATCH\|DELETE /admin/colleges`, `/admin/departments` (academic or non-academic), `/admin/programs`; renames and moves follow into the folders, and a unit can be deleted only while it holds nothing (409 `unit-in-use` lists what's left) |
| Users & roles (admin) | `/admin/users` (list, filter by role/status, add, edit, deactivate), `/admin/administrators`, `/admin/vpaf` (one holder), `/admin/programs/{id}/faculty` (+ `/primary`) |
| Settings | `GET /document-types`, `/submission-types`, `/academic-years`, `/academic-terms/active`; admin `/settings/document-types`, `/settings/academic-terms` (activate, close), `/settings/submission-types`, `/settings/retention-policies` |
| Teaching loads | `GET /teaching-loads?termId` (admin, VPAF), `POST /teaching-loads/import` (admin; CSV or .xlsx, preview then apply, replaces that term's load), encoding by hand: `GET /teaching-loads/courses?programId`, `PUT /teaching-loads/lines` (a person's sections of one course), `DELETE /teaching-loads/lines?termId&userId&courseId`; `GET /me/courses?termId&submissionTypeId` (the caller's courses, with any live submission per course) |
| Requirements | `/scopes/{college\|department\|program}/{id}/requirement-sets`, `/requirement-sets/{id}` (+ `/publish`), `/requirement-scopes/tree`, `/requirement-sets/effective` |
| Library | `/folders/tree`, `/folders/me`, `/folders/{id}` (+ `/children`), `/documents/{id}` (+ `/versions`, `/versions/compare`, `/content`, `/archive`, `/restore`), `/admin/org-sync/folders` |
| Access | `/folders/{id}/access`, `/documents/{id}/access`, `…/inheritance`, `/access/principals` |
| Uploads | `POST /uploads` → signed PUT URL, `PUT\|GET /storage/objects?token=` (anonymous, signed), `POST /uploads/{id}/complete` |
| Workflow | `/submissions` (+ `/attachments`, `/submit`, `/resubmit`, `/withdraw`, `/open`, `/approve`, `/request-revision`, `/reject`, `/reassign`, `/timeline`), `/reviews/pending`, `/dashboard/submissions` (AS / Dean / VPAF), `/notifications` |
| Search & AI | `GET /knowledge/search` (keyword), `GET /knowledge/assistant/status`, `GET /knowledge/passages` (retrieval only), `POST /knowledge/chat` (server-sent events) |
| Admin | `/admin/processing-jobs` (+ retry), `/admin/indexing-status` (with the search index), `POST /admin/indexes/rebuild` (202), `/admin/audit` |
| Development | `GET /dev/users`, `POST /dev/token` (anonymous, Development only) |

### Review routing

Stage 1 goes to the program's academic supervisor. If the program has no supervisor, or the
supervisor is the submitter, the college dean reviews instead. Department-level academic items go to
the dean, and a non-academic department's items go to its head, with a system admin as the last
resort. Accreditation and policy revisions add a dean stage. Nobody reviews their own submission.
Approval publishes the reviewed versions and grants the submitter's program or department View.

### Teaching loads and per-course types

Each term's load (faculty × program × course, optional section) comes from the registrar's file.
Re-importing a term replaces that term's lines only; people and courses not yet registered are
added, and each program someone teaches in becomes one of their programs. A load can also be encoded
by hand, one person's course at a time; a later import replaces those lines with the rest. Documents, folders and
other terms are never changed. A submission type marked "one submission per course" (by default:
SYLLABUS, LAB) must be program-scoped: the faculty picks a course from their load for the term, the
checklist comes from that course's program, there is one live submission per course, and its term
documents are filed in a course folder under the term (`Faculty › AY › Term › CS 301 · Data
Structures`). The dashboard counts a program's faculty from the term's load when it has one, and
expects one per-course submission per course taught.

## HTTP conventions (design frame 12)

- Errors are ProblemDetails with `type` `/problems/{code}`, a `code` and a `traceId`:
  400 validation, 401, 403 (visible but not allowed), 404 (missing *or not visible*), 409 conflict /
  stale version, 412 If-Match mismatch, 422 business rule or invalid transition, 501 feature not enabled.
- Editable resources return an `ETag`; send it back as `If-Match` when changing them.
- Upload complete, submit, resubmit and review decisions accept an `Idempotency-Key`; a retry with
  the same key replays the first response (`Idempotent-Replayed: true`).
- Lists are paged: `?page=&pageSize=` → `{ items, page, pageSize, total }`.
- JSON numbers are strict (no `"12"` for 12), so the OpenAPI document types integers as integers.

The frontend generates its TypeScript types from `/openapi/v1.json` (`npm run api:types`), so schema
names must be unique: `Program.cs` publishes Workflow's `PersonRef` and `VersionRef` as
`WorkflowPersonRef` and `WorkflowVersionRef`, because Library has records with the same names.

## AI: semantic search and the assistant

Design frames 5 (assistant), 6 (admin), 8 and 25 (ingestion), 10 (retrieval). With `Ai:Enabled` on,
processing continues past Extract into Chunk → Embed → Index, and the assistant answers from the
indexed passages with citations.

| Service | Used for |
|---|---|
| Ollama with `bge-m3` | Dense embeddings (1024 dims) through the OpenAI-compatible `/v1/embeddings` |
| Qdrant | Hybrid index: dense + BM25 sparse vectors in `kb_chunks_v{n}`, searched through the alias `kb_chunks` |
| Ollama with `qwen3.5:4b` | Answers, by default (`Ai:Chat:Provider=Local`), through `/v1/chat/completions`. Nothing leaves the machine. Ollama's context window must hold the passages: compose.yaml sets `OLLAMA_CONTEXT_LENGTH=16384`, because the OpenAI API can't set it per request and Ollama's default (4096) would cut passages silently |
| Claude (`claude-opus-5-5`) | Answers instead, with `Ai:Chat:Provider=Anthropic`. Needs `ANTHROPIC_API_KEY` (or `Ai:Chat:Anthropic:ApiKey`); without it chat streams the sources, then an error event |

A small local model handles prose well, and in Filipino too, but it is weaker than Claude at lining up
values in tables, especially tables that were extracted column by column. Choose the model by GPU
memory, which it shares with `bge-m3`: `qwen3.5:4b` for about 6 GB, `qwen3.5:2b` for less,
`qwen3.5:9b` with 12 GB or more.

**Ingestion** (`DocumentProcessor`, `Infrastructure/Ai`):

1. Extract: PdfPig reads each page with its layout. Tables are taken out first and read row by row
   (`cell | cell | cell`), Docstrum blocks and reading order handle the rest, running headers and
   page numbers are dropped, and blocks are typed as headings, paragraphs, list items or tables.
   DOCX, Markdown, text and CSV give the same blocks. Pages with almost no text but an image are
   flagged for OCR. Writes `derived/{versionId}/pages-p{n}.jsonl`.
2. OCR: the flagged pages, and PNG/JPEG uploads, are read by Tesseract (see [OCR](#ocr)) and replace
   what Extract found on them. Writes `ocr-p{n}.jsonl`, all pages, which becomes the version's text.
3. Chunk: structure first. Units (headings, paragraphs, list items, table rows) are packed into
   500–800-token chunks that stay inside their section, with 80 tokens of sentence overlap when a
   section continues. Each chunk keeps its pages and heading path. Writes `chunks-p{n}.jsonl`
   (`chunks-p{n}-ocr.jsonl` when cut from OCR text, so chunks cut before OCR are never reused).
4. Embed and index: title + section + passage is embedded, upserted with a BM25 sparse vector under a
   deterministic UUIDv5 point id, and the payload carries the ACL principals and publication state.

If Ollama or Qdrant is down, the job still finishes ("deferred") and the version stays Ready. The
index worker (`SearchIndexWorker`, every `Ai:IndexSyncSeconds`, one host at a time through a
PostgreSQL advisory lock) catches it up, re-syncs payloads when access or publication changes, and
runs index builds.

**Rebuilds.** `POST /api/admin/indexes/rebuild` (admin) queues a new collection. The worker fills it
from the chunk checkpoints, swaps the alias, and drops the old one; search stays on the old build
until then. A change of embedding model or dimensions starts a build by itself. After changing the
extractors or `Ai:Chunking`, bump `Processing:PipelineVersion` so every version is derived again.

**Questions.** `GET /api/knowledge/passages` runs retrieval alone; `POST /api/knowledge/chat` streams
`text/event-stream` events `sources`, `delta`…, then `done` (or `error`). Both take a scope
(`published`, or `my-drafts` for the caller's own unpublished versions) and optional folder, type,
year and term filters. Every hit from Qdrant is checked again against PostgreSQL permissions before
it is shown or sent to the model. When the best passage scores under `Ai:Retrieval:MinScore`, the
answer is "not enough evidence" and the model isn't called; an answer without valid citations is
treated the same way. Each question writes an `ai_query_audits` row, and requests are limited per
user (429 `rate-limited` with `Retry-After`). In Docker, nginx passes the chat stream on unbuffered
and waits up to 6 minutes for it, longer than its 60-second default, because a local model that has
to load first can stay silent for a while after the `sources` event.

### OCR

Scanned pages and photos of documents are read by Tesseract 5 (`Infrastructure/Ocr`), on the CPU and
offline. The API's Docker image installs it with English, Filipino and page-orientation data, and
poppler's `pdftoppm`.

- **What is read:** a PDF page with less than `Ai:Chunking:LowTextChars` characters of text and an
  image (or no text at all), and every PNG or JPEG upload. Pages with a text layer are never
  rendered, so born-digital PDFs cost nothing extra.
- **How:** `pdftoppm` renders the page in grey at `Ocr:Dpi`, and Tesseract reads it with orientation
  detection (`--psm 1`), so sideways and upside-down scans read too. Its TSV output becomes the same
  blocks as Extract's: Tesseract's paragraphs, headings by line height or capitals, list items by
  their markers. Words under `Ocr:MinWordConfidence` and page numbers in the margins are dropped.
- **Cost:** a second or two a page, `Ocr:Parallelism` pages at once. A 5-page work plan printed to
  images at 200 dpi (no text layer) came back with 99% of its words in 6.5 seconds.
- **Job notes:** the OCR stage records the engine, pages, characters, mean word confidence (below
  about 70 the scan is poor and search may miss words), time, and any pages that failed or were over
  `Ocr:MaxPages`. A page OCR can't read keeps its extracted text; when no page could be read the
  stage fails and the job retries, then waits in Admin → Processing for a manual retry.
- **Without Tesseract** (e.g. an API on a Windows host) the stage records "skipped: OCR engine not
  configured" with the reason. When an API starts with OCR available, `OcrCatchUpWorker` requeues
  every finished job that skipped OCR that way, and images processed before OCR existed, so earlier
  scans are read and indexed without a pipeline-version bump.
- **On a Windows host**, install Tesseract (the UB Mannheim build, with the Filipino data) and poppler,
  and set `Ocr:TesseractPath` and `Ocr:PdfToPpmPath` unless both are on the PATH.

## What is designed but not built yet

| Designed | Now |
|---|---|
| RabbitMQ between API and workers | Transactional outbox + in-process dispatcher (`IOutboxHandler`) |
| Separate Worker host | Workers run inside the API (`AddKnowledgeBaseWorkers()`) |
| SIS calendar sync | `POST /api/settings/academic-terms/sync` → 501 |

## Adding an MCP server later

Core doesn't depend on ASP.NET or HttpContext, so an MCP server is another host:

1. `dotnet new console -n KnowledgeBase.Mcp -o src/KnowledgeBase.Mcp` and add it to the solution;
   reference `KnowledgeBase.Core` and the official MCP C# SDK.
2. Call `services.AddKnowledgeBaseCore(configuration, contentRoot)` (skip `AddKnowledgeBaseWorkers()`
   if the API already runs them).
3. Register an `IIdentitySource` that maps the MCP client's authenticated identity to a user's
   `external_id` — authorization then works exactly as in the API.
4. Expose tools that call the existing services (e.g. search documents, get a document, list my
   submissions, effective checklist). Never query `KbDbContext` directly from a tool, so permission
   checks can't be skipped.

## Database

- Migrations live in `src/KnowledgeBase.Core/Infrastructure/Persistence/Migrations`:
  `dotnet ef migrations add <Name> --project src/KnowledgeBase.Core --startup-project src/KnowledgeBase.Api`
- The initial migration also installs triggers (`DatabaseTriggers.cs`): review actions and audit
  events are append-only, a version's file never changes, and a document's year/term match its
  type's tag.
- Tests build the model and the create script without a database: `dotnet test`.
