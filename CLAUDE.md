# cafs - Cloud API File System

## プロジェクト概要
Windows CfApi + 独自REST APIによるモダンファイル共有システム。

## 構成
- `server/` — Deno + TypeScript (Hono, Deno KV)
- `client/` — C# .NET 8 (CfApi, Vanara.PInvoke.CldApi)

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
dotnet build       # ビルド
dotnet run --project src/Cafs.Client  # 実行 (Windowsのみ)
```

## コーディング規約
- サーバー: TypeScript strict mode, Hono標準パターン
- REST APIのパスは全てフォワードスラッシュ、ストレージルート相対
- パス検証必須: `..` やnullバイトを拒否
- Deno KVのatomic操作で競合を防止
