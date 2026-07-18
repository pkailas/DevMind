# DevMind — LLM Server & Services Setup Guide (Reference Stack)

DevMind is endpoint-agnostic: anything that speaks the OpenAI-compatible API
works. This guide documents a complete, known-good **reference stack** — the
configuration DevMind is developed and daily-driven on — so you can reproduce
it rather than assemble one from scratch.

The reference machine runs Windows with an NVIDIA RTX PRO 6000 Blackwell
Workstation Edition (96 GB VRAM), models stored on a local SSD (`G:\models\...`),
and llama.cpp built locally. Adjust paths, hosts, and VRAM-sensitive flags to
your hardware — smaller cards mean smaller quants, shorter context, or partial
offload.

## The pieces at a glance

| Service | Port / location | Role |
|---|---|---|
| llama-server — chat model (Qwen3.6-27B) | `:8080` | The brain: TUI chat, MCP headless agent |
| llama-server — embedding model (Qwen3-Embedding-8B) | `:8082` | Embeddings for `/library` RAG |
| vLLM — vision model (Qwen3-VL, optional) | `:8084` | Dedicated PDF-page ingest for `/library` |
| SearXNG (self-hosted) | `http://vard-nas:8180` | `web_search` tool |
| Fetcher service (self-hosted) | `http://vard-nas:8181` | `web_fetch` tool |
| SQL Server 2025 | `WIN-SQL002,14330` | `/library` vector store (+ optional session history) |
| Roslyn language server | dotnet global tool | LSP tools (diagnostics, go-to-def, references, …) |
| netcoredbg | `%LOCALAPPDATA%\netcoredbg` | `/debug` (see the TUI Installation Guide) |

Launcher scripts for the model servers live in a `llm-launchers\` folder; the
ones referenced below are the current daily drivers.

---

## 1. Chat model — Qwen3.6-27B on llama-server (port 8080)

Two working launcher variants, both 262,144-token context, fully GPU-offloaded
(`-ngl 99`), with MTP (multi-token prediction) speculative decoding:

**Daily driver** (`DAILYDRIVERQwen3.6-MTP.bat`) — custom ik_llama MTP build,
Q8_0 MTP model, thinking disabled at the template level:

```bat
@echo off
title Qwen3.6-27B-Q8_0 — llama-server MTP (Blackwell)
set CUDA_VISIBLE_DEVICES=0
G:
cd G:\ik_llama_gemma4_mtp\build\bin\Release
.\llama-server.exe ^
  -m "G:\models\QWEN3.6\Qwopus3.6-27B-v2-MTP-Q8_0.gguf" ^
  --spec-type mtp ^
  --draft-max 2 ^
  --port 8080 ^
  --host 10.0.0.15 ^
  -ngl 99 ^
  --cache-ram 0 ^
  -fa on ^
  --ctx-size 262144 ^
  --cache-type-k q8_0 ^
  --cache-type-v q8_0 ^
  --parallel 1 ^
  --no-mmap ^
  --jinja ^
  -n 32768 ^
  --temp 1.0 ^
  --top-p 0.95 ^
  --min-p 0.0 ^
  --reasoning-budget -1 ^
  --chat-template-kwargs "{\"enable_thinking\":false}"
```

**Mainline + vision** (`start-qwen36-mtp-fast.bat`) — mainline llama.cpp with
the vision projector loaded, so the *same* endpoint serves `/image` multimodal
turns; draft-MTP speculation, f16 KV cache:

```bat
@echo off
title Qwen3.6-27B-Q8_0 — Mainline llama-server MTP PURE + Vision
set CUDA_VISIBLE_DEVICES=0
G:
cd G:\llama_cpp_mainline\build\bin\Release
.\llama-server.exe ^
  -m "G:\models\QWEN3.6\Qwen3.6-27B-Q8_0.gguf" ^
  --mmproj "G:\models\QWEN3.6\mmproj-F16.gguf" ^
  --spec-type draft-mtp ^
  --spec-draft-n-max 3 ^
  --spec-draft-p-min 0.75 ^
  -t 4 -tb 4 -b 2048 -ub 2048 ^
  --port 8080 ^
  --host 0.0.0.0 ^
  -ngl 99 ^
  --cache-ram 4096 ^
  -fa on ^
  --ctx-size 262144 ^
  --cache-type-k f16 ^
  --cache-type-v f16 ^
  --parallel 1 ^
  --no-mmap ^
  --jinja ^
  -n 32768 ^
  --temp 0.6 ^
  --top-p 0.95 ^
  --min-p 0.05 ^
  --reasoning-budget -1
```

Notes:

- **Thinking is off by default** for agentic work (the daily driver disables it
  via `--chat-template-kwargs`; DevMind's task tools also default `think=false`)
  — reasoning runs unbounded on a local server and can add minutes per
  iteration. Turn it on per-task, not globally.
- `--parallel 1` is deliberate: DevMind's MCP job queue runs one task at a time
  on a single GPU — a queue beats KV-cache thrash.
- If you serve on a LAN address (`--host 10.0.0.15` / `0.0.0.0`), set
  `DEVMIND_ENDPOINT` accordingly on any machine that connects. DevMind's own
  defaults assume `http://127.0.0.1:8080/v1` for the MCP/agent side.
- `/image` and `/digest` need the vision variant (mmproj loaded) or a separate
  vision endpoint — a text-only server will reject multimodal turns.

## 2. Embedding model — Qwen3-Embedding-8B (port 8082)

A **second** llama-server instance, run in embeddings mode, powering the
`/library` RAG pipeline (`start-qwen3-embedding.bat`):

```bat
title Qwen3-Embedding-8B — llama-server embeddings (port 8082, for DevMind /library)
set CUDA_VISIBLE_DEVICES=0
G:
cd G:\llama_cpp_mainline\build\bin\Release
.\llama-server.exe ^
  -m "G:\models\Qwen3-Embedding-8B\Qwen3-Embedding-8B-Q6_K.gguf" ^
  --embeddings ^
  --pooling last ^
  --port 8082 ^
  --host 0.0.0.0 ^
  -ngl 99 ^
  -c 8192 ^
  -b 8192 ^
  -ub 8192 ^
  --no-mmap
pause
```

How DevMind uses it: `EmbeddingClient` calls the OpenAI-compatible
`/v1/embeddings` endpoint, then **truncates each vector to 1024 dimensions and
L2-renormalizes** — Qwen3's embeddings are Matryoshka-style, so a truncated
prefix is still a valid (slightly lower-fidelity) embedding, and it fits SQL
Server 2025's `VECTOR(1024)` column. Text chunking respects the embedding
server's 8192-token context. Configure the endpoint via
`libraryEmbeddingEndpoint` in `%APPDATA%\devmind\devmind.json`
(default `http://127.0.0.1:8082/v1`).

## 3. Vector store — SQL Server 2025 (`/library`)

`/library` persists document chunks in SQL Server 2025+, which is required for
the native `VECTOR` type; search is `VECTOR_DISTANCE('cosine', …)`. The
reference config points at a LAN SQL box:

```json
// %APPDATA%\devmind\devmind.json
"libraryConnectionString": "Server=WIN-SQL002,14330;Database=DevMindRAG;Integrated Security=true;TrustServerCertificate=true"
```

Create an empty database (e.g. `DevMindRAG`); DevMind creates its `lib.*`
tables (`Documents`, `Chunks` with `Embedding VECTOR(1024) NOT NULL`) on first
ingest. Blank connection string = `/library` disabled. If either the embedding
server or SQL is unreachable, the tools fail gracefully with an
"is the embedding server running?" style message rather than crashing the
session.

Optional related settings in `devmind.json`:

- `sqlConnections` — named connection strings for the `query_db` tool (stored
  in config, never logged or echoed).
- Session history via `DEVMIND_HISTORY_*` env vars:
  `DEVMIND_HISTORY_ENABLED`, `DEVMIND_HISTORY_PROVIDER`
  (`sqlserver`/`sqlite`/`none`), and `DEVMIND_HISTORY_CONNECTION_STRING`
  (SqlServer) or `DEVMIND_HISTORY_DB_PATH` (Sqlite). Unset = no history.

## 4. Dedicated vision endpoint for PDF ingest (optional, vLLM)

By default `/library add` sends PDF page images to the main chat model. For
heavy ingest, a dedicated vision model gives better page notes without tying up
the chat server. The reference stack uses Qwen3-VL-32B-Instruct-AWQ on vLLM
(port 8084):

```bat
set HF_TOKEN=<your-huggingface-token>
set HF_HOME=G:\hf-cache
call C:\vllm-venv\Scripts\activate.bat
python -m vllm.entrypoints.openai.api_server ^
  --model "QuantTrio/Qwen3-VL-32B-Instruct-AWQ" ^
  --served-model-name "Qwen3-VL-32B-Instruct-AWQ" ^
  --port 8084 --host 0.0.0.0 ^
  --dtype auto --quantization awq_marlin --trust-remote-code ^
  --max-model-len 28000 --gpu-memory-utilization 0.3 --max-num-seqs 4 ^
  --limit-mm-per-prompt "{\"image\": 1, \"video\": 0}" ^
  --mm-processor-cache-gb 0 --async-scheduling --enforce-eager ^
  --mm-processor-kwargs "{\"max_pixels\": 6422528, \"min_pixels\": 6422528}"
```

Wire it up in `devmind.json`:

```json
"libraryVisionEndpoint": "http://127.0.0.1:8084/v1",
"libraryVisionModel": "Qwen3-VL-32B-Instruct-AWQ"
```

(vLLM requires the model id to match `--served-model-name` exactly.) If the
vision endpoint is set but unreachable, the TUI warns and offers to fall back
to the main model.

> Blackwell/vLLM note: `--enforce-eager` (or a compilation-config with
> `mode: 0` + `FULL_DECODE_ONLY` CUDA graphs on newer builds) works around a
> Triton cudagraph bug that corrupts Qwen3-VL grounding boxes on
> Hopper/Blackwell GPUs. Keep it until your vLLM ships Triton 3.6.

## 5. Web search & fetch — SearXNG + fetcher (self-hosted)

The `web_search` and `web_fetch` tools depend on two self-hosted services and
fail gracefully (returning a `[tool error]` string to the model) when
unreachable:

| Tool | Env var | Reference default |
|---|---|---|
| `web_search` | `DEVMIND_SEARCH_URL` | `http://vard-nas:8180` (SearXNG) |
| `web_fetch` | `DEVMIND_FETCH_URL` | `http://vard-nas:8181` (fetcher) |

SearXNG is queried as
`<url>/search?q=…&format=json&language=en&safesearch=0`, so the instance must
have the **JSON output format enabled** in its settings. Results are capped at
20, default 10.

**Reference deployment** — both services run as Docker containers on a
Synology NAS (`vard-nas`), so they're up 24/7 regardless of which workstation
is using DevMind:

| Container | Image | Port mapping |
|---|---|---|
| `devmind-searxng` | `searxng/searxng:latest` | `8180 → 8080` (container) |
| `devmind-fetcher` | `devmind-tools-fetcher` (custom-built image) | `8181 → 8000` (container) |

The fetcher exposes a `/health` endpoint (`{"status":"ok"}`) you can probe to
confirm it's alive. Any SearXNG instance works; point `DEVMIND_SEARCH_URL` at
yours and enable the JSON format.

**Microsoft Learn — built in, nothing to deploy.** As of v1.0.332 DevMind has
native Learn tools: `learn_search`, `learn_fetch`, and `learn_code_search`,
available in the TUI, CLI, and headless agent alike. They call Microsoft's
hosted Learn MCP server directly (`https://learn.microsoft.com/api/mcp`,
override via `DEVMIND_LEARN_URL`) — no local service, no API key, and the
agent is prompted to prefer them over `web_search` for .NET/C#/Azure/SQL API
questions. SearXNG's general engines (google, bing, duckduckgo, brave,
startpage) and dev engines (github, mdn, npm, pypi, askubuntu, superuser,
google scholar, arxiv) still provide broad coverage for everything else.

## 6. Language server (LSP tools)

The semantic tools (`get_diagnostics`, `go_to_definition`, `find_references`,
`hover`, `find_symbol`) route through a real language server:

- **C# (default: Roslyn LS).** Resolution: `DEVMIND_LSP_SERVER_PATH` if set,
  otherwise the dotnet global-tool shim at
  `%USERPROFILE%\.dotnet\tools\roslyn-language-server.cmd`. Roslyn runs over
  stdio, loads the nearest `.sln`/`.slnx` via `solution/open`, and uses
  pull-model diagnostics.
- **C# alternative: csharp-ls.** Set `DEVMIND_LSP_SERVER=csharp-ls`
  (Roslyn is chosen when the variable is `roslyn` or unset). If the configured
  server path clearly belongs to the other implementation, it's ignored — the
  explicit selection wins.
- **TypeScript/JavaScript** (`.ts/.tsx/.js/.jsx/.mjs/.cjs`) is also supported
  via the standard TypeScript language server.

Install the Roslyn server as a dotnet global tool so the default shim exists;
no per-project configuration is needed.

## 7. Bringing it up (daily order)

1. Start the chat llama-server (`:8080`) — nothing agentic works without it;
   DevMind's `devmind_task_start` health-probes it and refuses jobs if down.
2. Start the embedding server (`:8082`) if you'll use `/library`.
3. Start the vision vLLM (`:8084`) only for PDF ingest sessions.
4. SearXNG, the fetcher, and SQL Server run 24/7 on their own boxes (NAS /
   SQL host) — nothing to start per-session.
5. Launch `devmind` (TUI) or let your MCP client spawn
   `DevMind.McpServer.exe` on demand.

Everything is optional except #1: each subsystem degrades gracefully when its
service is absent — search/fetch return tool errors the model can read,
`/library` explains what's missing, and LSP tools report the server couldn't
start.

---

*DevMind is a product of iOnline Consulting LLC.*
