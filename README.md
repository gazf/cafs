# cafs - Cloud API File System

Samba/WebDAV/LDAPに依存しない、モダンなファイル共有システム。
Windows CfApi (Cloud Files API) + 独自REST APIサーバーで構成。

## アーキテクチャ

```
Windows エクスプローラー
  ↓ 標準ファイルシステムAPI
CfApi (cldflt.sys カーネルミニフィルタ)
  ↓ コールバック
cafs client (C# .NET 8)
  ↓ HTTPS
cafs server (Deno + TypeScript)
  ├── 認証・認可（Deno KV、JWT/トークン）
  ├── ファイル操作（ローカルFS）
  ├── ファイルロック管理
  └── 監査ログ
```

## セットアップ

### サーバー (Linux/WSL2)
```bash
cd server
deno task seed   # 初期データ投入
deno task dev    # 開発サーバー起動
```

### クライアント (Windows)
```bash
cd client
dotnet build
dotnet run --project src/Cafs.Client
```
