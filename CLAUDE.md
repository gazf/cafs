# cafs - Cloud API File System

## プロジェクト概要

Windows CfApi (Cloud Files API) を利用したモダンなファイル共有システム。
サーバが配信するファイルツリーをクライアントが placeholder として展開し、
on-demand hydration / write-back / WSS 経由のリアルタイム同期で動作する。

## 構成

- `server/` — Deno + TypeScript (Hono, Deno KV)
- `client/` — C# .NET 10 / Windows Forms。Clean Architecture 5 層構成。
  - `CfApi.Native` — `cldapi.dll` の P/Invoke 宣言と unsafe 構造体・enum
  - `CfApi.Interop` — `[UnmanagedCallersOnly]` のディスパッチ・unsafe → managed 変換・
    OplockFileHandle などのマネージドラッパー
  - `Cafs.Core` — ドメインモデル・抽象 (`ICafsServer`, `IEventStream`)・SyncEngine・
    `ISyncCallbacks` 実装 (`CafsSyncCallbacks`)
  - `Cafs.Transport` — REST/WSS の HTTP 実装 (`HttpCafsServer`, `HttpEventStream`)
  - `Cafs.App` — WinForms ホスト (TrayAppContext, SettingsForm)

## 現在の実装状況

- プレースホルダー: `CF_POPULATION_POLICY_ALWAYS_FULL` + WSS 駆動 ([ADR-013](docs/decisions.md))
- 読み取り: FETCH_DATA で on-demand hydration、レスポンスの `X-File-Attributes` で RO 反映 (ADR-019)
- 書き戻し: NOTIFY_FILE_CLOSE_COMPLETION で modified 検出 → ロック確保 (open 時に取得済み)
  → アップロード → `UpdatePlaceholder` でメタデータ同期 → `SetInSyncState(true)` → ロック解放
  → dehydrate
- ロック: open 時に Device ID 単位で取得 (ADR-016)、(userId, deviceId) で識別、
  Deno KV `expireIn` 30 秒 + WSS heartbeat 10 秒で延長 (ADR-018)。
  他 device の保持中ファイルは X-File-Attributes / WSS lock_acquired で RO 反映 (ADR-019)
- Device ID: クライアント実行ファイル同ディレクトリの `device.json` に永続化
- 認可: REST/WSS いずれも `checkPermission` でフィルタ

## サーバー開発

```bash
cd server
deno task dev      # 開発サーバー起動 (port 8700, --watch)
deno task test     # テスト実行
deno task seed     # 初期データ投入
```

## クライアント開発

```bash
cd client
dotnet build                       # ビルド
dotnet run --project src/Cafs.App  # 実行 (Windows のみ)
```

## コーディング規約

- サーバー: TypeScript strict mode, Hono 標準パターン
- REST API のパスは全てフォワードスラッシュ、ストレージルート相対
- パス検証必須: `..` や null バイトを拒否
- Deno KV の atomic 操作で競合を防止
- C#: `unsafe` は CfApi.Native/Interop の Internal に閉じ込め、上位層は managed のみ
- C#: zero-alloc 優先 (DTO は readonly struct, ArrayPool 利用, stackalloc + ArrayPool フォールバック)

## ドキュメント

- [docs/decisions.md](docs/decisions.md) — Architecture Decision Records

## ログ

- サーバ: `server/cafs-server.log`(`console.log/warn/error` を tee、追記モード)
- クライアント: `client/src/Cafs.App/bin/Debug/<TFM>/cafs-client.log`
  (`Trace.WriteLine` + 未捕捉例外を `[FATAL]`/`[ERROR]` で記録)
