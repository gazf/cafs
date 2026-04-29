# cafs - Cloud API File System

## Overview

Modern file-sharing system built on Windows CfApi (Cloud Files API). The server publishes a file tree; the client materializes it as placeholders and runs on-demand hydration, write-back, and real-time sync over WSS.

## Layout

- `server/` — Deno + TypeScript (Hono, Deno KV)
- `client/` — C# .NET 10 / Windows Forms. Clean Architecture, 5 layers:
  - `CfApi.Native` — `cldapi.dll` P/Invoke declarations and unsafe structs/enums
  - `CfApi.Interop` — `[UnmanagedCallersOnly]` dispatch, unsafe → managed conversion, managed wrappers like `OplockFileHandle`
  - `Cafs.Core` — domain models, abstractions (`ICafsServer`, `IEventStream`), `SyncEngine`, `ISyncCallbacks` impl (`CafsSyncCallbacks`)
  - `Cafs.Transport` — REST/WSS HTTP impl (`HttpCafsServer`, `HttpEventStream`)
  - `Cafs.App` — WinForms host (`TrayAppContext`, `SettingsForm`)

## Current implementation

- Placeholders: `CF_POPULATION_POLICY_ALWAYS_FULL` + WSS-driven ([ADR-013](docs/decisions.md))
- Read: on-demand hydration via FETCH_DATA; RO reflected from `X-File-Attributes` response header (ADR-019)
- Write-back: NOTIFY_FILE_CLOSE_COMPLETION detects modify → ensure lock (acquired at open) → upload → `UpdatePlaceholder` for metadata sync → `SetInSyncState(true)` → release lock → dehydrate
- Locking: acquired at open per Device ID (ADR-016); identified by `(userId, deviceId)`; TTL via Deno KV `expireIn` 30s + WSS heartbeat 10s (ADR-018). Files held by another device are flagged RO via `X-File-Attributes` and WSS `lock_acquired` (ADR-019)
- Device ID: persisted in `device.json` next to the client executable
- Authorization: REST and WSS both filter through `checkPermission`

## Server dev

```bash
cd server
deno task dev      # dev server (port 8700, --watch)
deno task test
deno task seed     # initial data
```

## Client dev

```bash
cd client
dotnet build
dotnet run --project src/Cafs.App  # Windows only
```

## Coding rules

- Server: TypeScript strict mode, idiomatic Hono patterns
- REST API paths: forward slashes, relative to storage root
- Path validation required: reject `..` and null bytes
- Use Deno KV atomic ops to prevent races
- C#: `unsafe` confined to `CfApi.Native`/`CfApi.Interop` internals; upper layers stay fully managed
- C#: prefer zero-alloc (DTOs as `readonly struct`, `ArrayPool`, `stackalloc` with `ArrayPool` fallback)

## Docs

- [docs/decisions.md](docs/decisions.md) — Architecture Decision Records

## Logs

- Server: `server/cafs-server.log` (`console.log/warn/error` tee, append mode)
- Client: `client/src/Cafs.App/bin/Debug/<TFM>/cafs-client.log` (`Trace.WriteLine` + uncaught exceptions logged as `[FATAL]`/`[ERROR]`)
