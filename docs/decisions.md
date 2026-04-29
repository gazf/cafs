# Architecture Decision Records

実装に大きな影響を与える設計判断を、決定事項・選択肢・選択理由を残す形で記録する。

---

## ADR-001: プロトコル選択 — CfApi + HTTPS

**決定**: Windows Cloud Files API(CfApi)+ HTTPS REST API を採用。

**却下した選択肢**:

- **WebDAV**: Microsoft が 2023 年に非推奨化、Windows 10/11 でデフォルト無効、将来削除予定
- **SMB / Samba 拡張**: プロトコル設計古い、Zero Trust と非整合、Windows カーネル実装への依存から脱却不可
- **独自プロトコル**: 既存ツール(curl、ブラウザ等)でデバッグできない

**選択理由**:

- CfApi は OneDrive 級の UX(エクスプローラー統合、オンデマンドハイドレーション)
- HTTPS 単一ポート(443)でファイアウォール・プロキシ越しでも動作
- TLS 1.3 で暗号化、Zero Trust と整合
- モダンな認証(OIDC、JWT)と統合しやすい

---

## ADR-002: Vanara.PInvoke.CldApi の不採用

**決定**: Vanara ライブラリを使わず、P/Invoke を自前実装する。

**却下理由**:

- ホットパスで `PinnedObject` + `Marshal.PtrToStructure` のヒープアロケが発生
- `DllImport` ベースで、`LibraryImport` や `UnmanagedCallersOnly` の利点を享受できない
- リフレクションベースのマーシャラで Native AOT に非対応
- 使わない数百関数がアセンブリに含まれる

**採用した代替**:

自前の `CfApi.Native` レイヤー。`LibraryImport`、`readonly struct`、関数ポインタ(`delegate* unmanaged`)、`Pack = 8` 明示等、モダン C# の機能をフル活用。

---

## ADR-003: レイヤー構造 — 5 層

**決定**: Native / Interop / Core / Transport / App の 5 層構造。

**各層の責務**:

- **CfApi.Native**: Win32 CfApi の P/Invoke 写像のみ。ビジネスロジックを持たない
- **CfApi.Interop**: Native 型と Domain 型の翻訳、コールバックディスパッチ。Native 型を外に漏らさない
- **Cafs.Core**: ドメインロジック、ユースケース、抽象インターフェース(`ICafsServer`、`IEventStream` 等)
- **Cafs.Transport**: HTTP / WSS / SSE 等の通信実装
- **Cafs.App**: WinForms UI、設定、DI 組み立て、エントリポイント

**依存方向**:

```
Native ← Interop ← Core ← App
                    ↓
                 Transport → Deno Server
```

**選択理由**:

- 責務が明確、Clean Architecture 準拠
- Core は CfApi/HTTP の具体実装を知らない(テスト容易、移植容易)
- 将来の CLI / Web UI 追加時に Core / Transport を再利用可能

---

## ADR-004: オフライン時の挙動 — 読み取り専用または使用不可

**決定**: オフライン時は編集不可。環境に応じて「読み取り専用」または「使用不可」を選ぶ。

**却下した選択肢**:

- **オフライン編集 + 復帰時コンフリクト解決**: Nextcloud / OneDrive 的なアプローチ。コンフリクト発生源、UX を損なう

**選択理由**:

- コンフリクトを原理的に発生させない
- 監査・コンプライアンスがクリーン
- 実装が大幅にシンプル(UploadQueue、ConflictResolver が不要)
- 社内ファイルサーバー代替として妥当な割り切り

**実装方針**:

- ネットワーク状態監視
- オフライン時は FETCH_DATA で `STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE` を返す
- タスクトレイで状態表示

---

## ADR-005: コンフリクト解決 — サーバー側ロック(初版)

**決定(初版)**: ファイル open 時にサーバー側ロックを取得、close + アップロード完了後に解放する。

**排他制御の 3 層**:

1. 論理ロック(サーバー側 KV): 他ユーザーへの編集中通知
2. ETag / If-Match(HTTP): ロック漏れの保険、並行 PUT を防ぐ
3. atomic rename(ファイルシステム): 書き込み中の中途半端なデータを他ユーザーから隠す

**現状**:

本 ADR は Phase 5 実装中に再評価され、**ADR-016 / ADR-018 で更新**された。SID ベース Liveness 管理 + コンフリクトファイル戦略(ADR-017)を採用している。

**関連 ADR**: ADR-016, ADR-017, ADR-018

---

## ADR-006: イベント通知 — WebSocket(WSS)採用

**決定**: WebSocket(WSS)で差分イベントをサーバーからプッシュ受信する。

**却下した選択肢**:

- **ポーリング**: リアルタイム性が出ない、サーバー負荷が高い
- **SSE 単独**: サーバーからの一方向のみ、双方向通信ができない

**SSE を補助に残す可能性**:

Cloudflare Tunnel 環境で WSS のアイドルタイムアウト(Free/Pro で 100 秒)が問題になる場合、SSE フォールバックを検討。現時点では WSS のみで実装。

**選択理由**:

- リアルタイム差分受信が可能
- 双方向通信(将来 ADR-018 のハートビート等)に活用可能
- 標準的なプロトコル、デバッグ容易

**関連 ADR**: ADR-013(本決定で WSS を正式採用), ADR-018(WSS で SID ハートビートを送る)

---

## ADR-007: 認証 — OIDC + JWT

**決定**: LDAP / Kerberos を使わず、OIDC(OpenID Connect)経由で認証、JWT で認可。

**選択理由**:

- Google Workspace、Microsoft 365、Okta、Keycloak 等と直接統合可能
- MFA 標準対応
- JWT で HTTP / WebSocket 両方に同じ認証機構
- ステートレス、スケール容易

**JWT クレーム設計**:

```json
{
  "sub": "user-id",
  "device_id": "laptop-abc123",
  "iat": 1234567890,
  "exp": 1234571490
}
```

短命(1 時間)+ リフレッシュトークン。デバイス ID も含め Zero Trust 対応。

---

## ADR-008: 権限モデル — パス + プリンシパル × アクション

**決定**: 「ユーザー / グループ」×「パス」×「read / write / admin」の細粒度認可。

**Deno KV スキーマ**:

```
["users", userId]                   → { id, name, email, groups }
["groups", groupId]                 → { id, name, members }
["permissions", path, type, id]     → { path, principal, access }
```

**アクセスチェック**:

- パスの親階層まで遡って判定
- ユーザー自身 + 所属グループの権限をチェック
- いずれかで許可があれば OK

---

## ADR-009: Zero Alloc の適用範囲

**決定**: モダン C# の機能は活用するが、過剰な最適化は避ける。

**適用する**:

- `LibraryImport` による source-generated マーシャラ
- 構造体の `readonly struct` 化
- 関数ポインタ(`delegate* unmanaged<>`)、Delegate 不使用
- `UnmanagedCallersOnly` コールバック
- ホットパスの `stackalloc` + `ArrayPool<T>` ハイブリッド

**適用しない(現時点)**:

- `ValueTask` 化(async 境界でメリット薄い)
- `PoolingAsyncValueTaskMethodBuilder`(効果が測れていない)
- カスタム `IValueTaskSource` 実装(オーバーエンジニアリング)
- UniTask 等の外部ライブラリ(標準機能で足りる)

**判断基準**: ボトルネックになってから最適化する。それまでは可読性・保守性優先。

---

## ADR-010: HTTP/2 の採用(将来)

**決定**: サーバー・クライアント両方で HTTP/2 を有効化する(Phase 5 完了後に実装)。

**理由**:

- `HttpClient` の設定で簡単に有効化可能
- ヘッダ圧縮(HPACK)で認証トークン繰り返しのオーバーヘッド削減
- 多重化でメタデータ取得の並列化
- HTTP/1.1 フォールバック自動対応

**HTTP/3 は見送り**:

- Deno、.NET 両方で実装が若い
- UDP 443 の企業ファイアウォール通過性に懸念

---

## ADR-011: テスト戦略

**決定**: サーバー側はインメモリ KV を使ったユニットテスト、クライアント側は実機テスト中心。

**サーバー側**:

- Deno 標準テスト + `:memory:` KV で独立テスト
- CI で `deno task test` を回す

**クライアント側**:

- Domain 層(Cafs.Core)はモック化してユニットテスト可能
- Interop 層以下は実機テスト必須(CfApi が Windows カーネル依存)
- E2E: Windows 実機で Explorer から操作確認

---

## ADR-012: OpenAPI / TypeSpec の不採用(現時点)

**決定**: API スキーマ定義ファイル(OpenAPI、TypeSpec)は現時点では導入しない。

**却下理由**:

- API がまだ流動的、スキーマ定義のメンテコストが高い
- AI によるコード生成で型合わせが可能
- 手書き OpenAPI の価値は激減

**将来採用する条件**:

- API が安定してきた
- 他言語クライアント(macOS、iOS、Web)を作る予定が出てきた
- エンタープライズ顧客から仕様書提出を求められる

---

## ADR-013: プレースホルダー戦略 — ALWAYS_FULL + WSS イベント駆動

**決定**: 起動時に `/tree` で全ツリーを取得し、`CF_POPULATION_POLICY_ALWAYS_FULL` でプレースホルダーを一括作成、WSS で差分プッシュ受信する。

**却下した選択肢**:

- **PARTIAL + FETCH_PLACEHOLDERS によるオンデマンド**: ディレクトリ展開時のレイテンシで Explorer が固まる、UX が劣る
- **ポーリングベースの差分同期**: WSS のリアルタイム性が出ない、サーバー負荷が高い

**選択理由**:

- エクスプローラー操作が即座に反応する(オフラインファイルと同じ感覚)
- WSS で他クライアントの変更がリアルタイム反映
- 実機検証で UX が劇的に改善することを確認

**実装上の知見(実機検証由来)**:

- ALWAYS_FULL でも OS が `FETCH_PLACEHOLDERS` を送ることがあるため、空リストで即答するハンドラーを残す必要あり
- 再起動時に `CfCreatePlaceholders` が `ERROR_ALREADY_EXISTS (0x800700B7)` を返すのは正常動作として扱う
- WSS は Deno が `for await` ループ break 時に watcher を自動クローズするので、`onclose` での `watcher.close()` は二重クローズになる。`closeWatcher` ガードで防ぐ
- 起動時の `CreatePlaceholders` だけでは Explorer に表示されない。`SHCNE_UPDATEDIR` を打って再列挙を促す必要あり

**スケーラビリティの制約**:

- 数万ファイル超える場合、起動時の `/tree` 取得と `CreatePlaceholders` に時間がかかる
- 現実的上限は 10万ファイル程度
- それ以上のスケールが必要になったら、ルート直下のみ ALWAYS_FULL、サブディレクトリは PARTIAL のハイブリッド戦略へ移行を検討

**フォールバック / 再接続**:

- WSS 切断時は 5 秒バックオフで自動再接続(`RunEventLoopWithReconnectAsync`)
- イベント取りこぼし時は手動の `OnSyncNow` で全同期回復

**関連 ADR**:

- ADR-006(イベント通知): 本決定で WSS を正式採用

---

## ADR-014: ハイブリッド修正検出 — open/close ウィンドウ + 同期時刻ベース

**決定**: ファイル変更検出を 2 段階で行う。

1. **第1検出**: open/close ウィンドウ内で `LastWriteTimeUtc` が変化したか
2. **第2検出**: 最後に同期した時刻(`LastSyncedWriteTimes`)より新しい `LastWriteTimeUtc` か

どちらかが真なら `isModified = true` としてアップロード対象にする。

**背景**:

Notepad の `save-to-temp+rename` 保存や autosave は、OPEN/CLOSE のコールバックウィンドウ**外**で書き込みが発生する。第1検出だけでは漏れる。

**実装**:

`SyncContext.LastSyncedWriteTimes`(`ConcurrentDictionary<string, DateTime>`)を追加し:

- FullSync 時にサーバの `lastModified` を記録
- WSS イベント(created/modified)受信時にも記録
- close → `safeToDehydrate=true` の場合に現在の `writeTime` を記録(アップロード成功 or 純粋 read)

これにより、OPEN/CLOSE を伴わない書き込み(rename 保存等)も次回 close 時に拾える。

**選択理由**:

- USN ジャーナルや FileSystemWatcher を使う方法より軽量
- 設計判断として「2 段階検出」がシンプルで理解しやすい
- 実機で Notepad での編集が漏れる症状を直接解決した

---

## ADR-015: oplock ハンドル開閉戦略

**決定**: `SetInSyncState` / `UpdatePlaceholder` のたびに `OplockFileHandle` を開閉する。ハンドルを保持し続けない。

**背景(実機で詰まった経緯)**:

書き戻しフローで「open + state 変更 + read + アップロード + state 変更 + close」を試みたところ、以下の問題が連鎖的に発生:

1. `CfOpenFileWithOplock` の handle は overlapped で開かれている
2. `FileStream` で読もうとすると:
   - `isAsync: true` → "BindHandle for ThreadPool failed"(CfApi が内部で完了ポートに bind 済み、再 bind 不可)
   - `isAsync: false` → "Handle does not support synchronous operations"
3. ハンドル保持中は `File.OpenRead` もシェアバイオレーション

**解決**: state 変更のたびに開閉する戦略

```csharp
using (var handle = OplockFileHandle.Open(localPath, OplockOpenFlags.WriteAccess))
    handle.SetInSyncState(false);

await using var stream = File.OpenRead(localPath);
await UploadAsync(stream);

using (var handle = OplockFileHandle.Open(localPath, OplockOpenFlags.WriteAccess))
{
    handle.UpdatePlaceholder(...);
    handle.SetInSyncState(true);
}
```

**残るレース**: `SetInSyncState(false)` と `File.OpenRead` の隙間で OS が dehydrate する可能性は理論上ある。ただし `SetInSyncState(false)` で OS の自動 dehydrate は抑制されているので**実害なし**。

**関連 ADR**:

- ADR-005: コンフリクト解決(3層排他制御)

---

## ADR-016: ロック取得タイミング — open 時 + Liveness ベース管理

**決定**: ファイル open 時にサーバー側ロックを取得する。Samba 同等の UX を実現し、コンフリクトを構造的に発生させない。Liveness 管理は Device ID + WSS ハートビート方式(ADR-018)、ロック中ファイルへの編集ブロックは X-File-Attributes ヘッダ方式(ADR-019)で実現する。

**経緯**:

- ADR-005 で「open 時にロック取得」を決定
- Phase 5 実装中に「読み取り目的のオープンでもロック発生 → サーバー負荷」を懸念し、一時的に「close 時のみロック」(楽観的方式)に変更
- 100 人規模での負荷試算で、ロック取得は問題にならないと判明
- 設計原則「Samba 同等、コンフリクトを許容しない」に立ち返り、open 時ロックに戻す

**100 人規模の負荷試算**:

- ロック取得 1 回 ≈ 5ms(JWT 検証 + checkPermission + KV.get + KV.atomic)
- Deno KV スループット ≈ 数千 ops/秒
- ピーク 50 ops/秒(100 ユーザー × 5 ファイル × 10 秒集中)
- 利用率 ≈ 13%、余裕あり

サーバー負荷を理由に「読み取りロック」を避ける必要はない。**真の論点は UX**(短時間プレビューで誤警告、異常終了でゴーストロック残存)であり、これは ADR-018 の TTL 30 秒 + WSS ハートビートで解決する。

**Samba 同等の UX**:

- ファイル open 時にロック取得 → 他ユーザーには「編集中」と即座に伝わる(WSS broadcast)
- 他ユーザーが同じファイルを開く → サーバーが ReadOnly 属性付きで返す(ADR-019)→ 編集アプリが RO を尊重して編集ブロック
- A が close → ロック解放 → 他ユーザーは編集可能になる

これにより**コンフリクトが構造的に発生しない**。

**保険機構**:

- 異常事態(WSS 取りこぼし、race condition、バグ等)でロック未取得のまま編集が成立してしまった場合 → conflict file で救済(ADR-017)
- 通常運用では発火しない、設計の最終保証

**関連 ADR**:

- ADR-005: 旧仕様(本 ADR で更新)
- ADR-017: コンフリクトファイル(異常事態の最終手段)
- ADR-018: Device ID + WSS ハートビートによる Liveness 管理
- ADR-019: X-File-Attributes ヘッダによる属性伝達
- ADR-020: close 時の常時 dehydrate

---

## ADR-017: コンフリクトファイル戦略 — 異常事態の最終手段

**決定**: cafs の設計思想は「コンフリクトを許容しない」(ADR-016 の open ロック + ADR-019 の RO 反映で構造的に防ぐ)。コンフリクトファイル機構は **通常運用では発火しない最終保証** として残す。

**位置付けの明確化**:

- ✅ 通常運用: ロック取得失敗時はファイルが RO で開かれる → 編集自体が発生しない → コンフリクトしない
- ⚠️ 異常事態のみ: WSS 取りこぼし、race condition(B が先に開いてから A がロック取得)、バグ等で編集が成立してしまった場合
- 異常事態でも**ユーザーの作業を絶対に失わない**ためのセーフティネット

**実装**:

ロック取得失敗時、または upload 時の整合性検証(ETag 不一致等)で衝突を検知した場合、ローカル変更を `<stem>.conflict-<yyyyMMdd-HHmmss><ext>` として退避し、元ファイルは dehydrate してサーバー版に戻す。

```csharp
private static async Task<bool> SaveAsConflictFileAsync(string relativePath, string localPath)
{
    var dir = Path.GetDirectoryName(localPath);
    var stem = Path.GetFileNameWithoutExtension(localPath);
    var ext = Path.GetExtension(localPath);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var conflictPath = Path.Combine(dir, $"{stem}.conflict-{stamp}{ext}");

    await using var src = File.OpenRead(localPath);
    await using var dst = File.Create(conflictPath);
    await src.CopyToAsync(dst);

    return true;
}
```

**運用上の意味**:

- 通常運用で conflict file が発生する = 設計上の異常事態 または バグの可能性
- ログ記録 + 監視対象として扱う
- 頻発する場合は WSS 接続安定性、ロック機構の見直しを検討

**選択理由**:

- データロスゼロ原則(ユーザーの作業を絶対に失わない)
- Dropbox / OneDrive と同じ方式(エンドユーザーが慣れている)
- 後からマージ作業が可能
- 元ファイルがサーバー版に同期されるので整合性が保たれる

**関連 ADR**:

- ADR-016: open 時ロック(通常はここで防ぐ)
- ADR-018: Device ID ベース Liveness ロック
- ADR-019: X-File-Attributes ヘッダ(編集ブロックの主機構)

---

## ADR-018: Device ID ベース Liveness ロック管理

**決定**: クライアント端末ごとに永続的な Device ID(UID, UUID v4)を生成・保管し、JWT 認証時に含めて検証、ロックは `(userId, deviceId)` で識別する。WSS ハートビートで TTL を延長、異常時は Deno KV の `expireIn` で自動解除、グレースフル終了時は WSS terminate で即時解除する。

**モデル**:

- **Device ID(UID)**: 端末初回起動時にクライアントが生成、Local AppData に永続化、再起動・再インストールまで変わらない
- **JWT**: 認証時にサーバー発行、`device` クレームに deviceId 含む、1 時間有効
- **ロック**: `(userId, deviceId, path)` で識別、TTL 30 秒、Deno KV `expireIn` で自動削除
- **WSS 接続**: ハートビート(10 秒間隔、ペイロードは deviceId のみ)で TTL 延長、リアルタイム通知の通り道

**動作フロー**:

```
[初回起動]
1. クライアント: 実行ファイルと同ディレクトリの device.json を確認
   - なければ UUID v4 生成、保存
   - あれば読み込み

[ログイン]
2. POST /auth/login { username, password, deviceId }
3. サーバー: 認証 → device テーブル登録/更新 → JWT 発行(device クレーム含む)

[WSS 接続]
4. クライアント: ws://server/events?token=<JWT>
5. サーバー: JWT 検証(device クレームと送信元の整合性確認)

[ファイル open]
6. POST /locks/* (Bearer JWT, X-Device-Id ヘッダ)
7. サーバー: deviceId で識別してロック保存(expireIn: 30s)
8. サーバー: WSS で全クライアントに lock_acquired broadcast

[ハートビート]
9. クライアント: WSS で { type: "heartbeat", deviceId } を 10秒ごと送信
10. サーバー: deviceId に紐づく全ロックの TTL を 30秒延長

[ファイル close]
11. クライアント: アップロード(編集あり時)
12. クライアント: DELETE /locks/* (X-Device-Id ヘッダ)
13. サーバー: deviceId 一致確認 → ロック削除
14. サーバー: WSS で lock_released broadcast

[グレースフル終了]
15. クライアント: WSS で { type: "terminate", deviceId } 送信
16. サーバー: deviceId に紐づく全ロック削除 + broadcast

[異常終了]
17. WSS 切断検知 → サーバーは何もしない(任意)
18. 30秒経過 → KV expireIn で各ロック自動削除
19. 自前スイーパーが TTL 切れを検知 → lock_released broadcast
```

**スキーマ**:

```typescript
// Deno KV
["devices", deviceId] → {
    deviceId: string,
    userId: number,
    label: string,         // "OFFICE-PC-01" 等
    firstSeenAt: string,
    lastSeenAt: string,
    ipAddress?: string,
}

["devices-by-user", userId, deviceId] → true  // 逆引き

["locks", filePath] → {
    userId: number,
    deviceId: string,      // ロック保持端末
    acquiredAt: string,
    expiresAt: string,     // 参考情報、実際の TTL は KV expireIn
}
```

**JWT クレーム**:

```json
{
    "sub": 42,
    "device": "uuid-v4-here",
    "iat": 1745568000,
    "exp": 1745571600
}
```

**永続化**:

- 保存先: クライアント実行ファイルと同ディレクトリの `device.json`(`AppContext.BaseDirectory` 直下)
- ID の単位は **インストール単位**。同一 PC でユーザーを切り替えても同じ ID、フォルダごと別 PC にコピーすれば移行も可能(ローミングプロファイルでの意図しない ID 共有は構造上発生しない)
- 単一実行ファイル発行(`PublishSingleFile`)でも `AppContext.BaseDirectory` は実行ファイルの位置を返すので安定
- 配置上の注意: `Program Files` 配下のような書き込み不可ロケーションには配置しない。ポータブル配置(ユーザー書き込み可能なフォルダ)を前提にする

```json
{
    "deviceId": "7c3e8a92-4f2b-4d89-a1e3-9f8c7b6d5a4e",
    "createdAt": "2026-04-25T10:00:00Z"
}
```

**同一ユーザー別端末の扱い**:

`(userId, deviceId)` で識別するが、**同一 userId なら別 deviceId に取り戻し許可**:

```typescript
async function acquireLock(filePath, userId, deviceId): LockResult {
    const existing = await kv.get<LockData>(Keys.lock(filePath));
    
    if (existing.value) {
        if (existing.value.userId === userId) {
            // 同一ユーザー → renew(同 deviceId)or 取り戻し(別 deviceId)
            const updated = { 
                userId, 
                deviceId,  // 新 deviceId に上書き
                acquiredAt: existing.value.acquiredAt,
                expiresAt: new Date(Date.now() + LOCK_TTL_MS).toISOString(),
            };
            await kv.set(key, updated, { expireIn: LOCK_TTL_MS });
            return { success: true, lock: updated };
        }
        // 他ユーザー → 拒否
        return { success: false, reason: "locked_by_other_user" };
    }
    
    // 新規取得
    return createLock(filePath, userId, deviceId);
}
```

OneDrive と同じ方式(「PC で開いていたファイルをノートで取り戻す」が可能)。

**選択理由**:

- **ID 永続性**: 再接続・再起動で deviceId は変わらない → 「session_restore」のような複雑なロジック不要
- **JWT との統合**: deviceId は JWT クレームとして検証、偽造防止
- **監査追跡容易**: 「いつ、どの端末から、何をしたか」が device 単位で永続的に追跡可能
- **同一ユーザー別端末対応**: deviceId 比較で自然に判別
- **ハートビート軽量**: ペイロードは deviceId のみ、固定サイズ
- **管理 UI 構築可能(将来)**: 「ログイン中のデバイス」一覧、強制ログアウト等

**100 人規模の負荷試算**:

- ロック取得: 1 回 ≈ 5ms、ピーク 50 ops/秒、Deno KV キャパ内
- ハートビート: 100 ユーザー × 10秒間隔 = 10 ops/秒、無視できる
- broadcast: 100 ユーザーへの WSS 送信、軽量

**異常終了からの回復**:

- WSS 切断 → 30 秒で KV TTL 切れ → ロック自動失効
- Samba(TCP 切断で即失効)に近い UX、HTTPS 上で実現

**却下した選択肢**:

- **クライアント生成セッション ID(WSS 切断で変わる)**: 再接続時の引き継ぎが複雑
- **サーバー発行セッション ID**: 複雑な session_restore プロトコルが必要
- **持っているロック一覧をハートビート送信**: ペイロード過大、SSOT 原則に反する
- **純 TTL のみ**: 異常時の即時検知ができない

**関連 ADR**:

- ADR-005: コンフリクト解決(本 ADR で具体化)
- ADR-016: ロック取得タイミング(open 時)
- ADR-019: X-File-Attributes ヘッダ(ロック状態の伝達)
- ADR-020: 常時 dehydrate

---

## ADR-019: ファイル属性をレスポンスヘッダで伝達

**決定**: ファイル取得時(GET /content/*)のレスポンスヘッダにファイル属性(`X-File-Attributes`)を含める。クライアントはヘッダを読んで CfApi 経由でローカル NTFS の MFT に反映する。`/tree` エンドポイントも同様に各ノードの属性情報を含める。

**設計の核**:

- HTTP のボディ = ファイル中身(不可侵)
- HTTP のヘッダ = メタ情報(属性、ロック状態、所有者等)
- 1 リクエストで全情報を取得、原子的、ズレなし

**動作**:

```
[ハイドレート時]
クライアント: GET /content/report.docx (X-Device-Id: <自分>)
サーバー: 
  - 認可チェック
  - ロック状態確認: holder.deviceId !== requester.deviceId なら "他人のロック中"
  - レスポンス:
      Content-Type: application/octet-stream
      ETag: "abc-123"
      Last-Modified: ...
      X-File-Attributes: ReadOnly        ← ロック中なら
      X-File-Lock-Holder: alice          ← 任意、ユーザー通知用
      [body: ファイル中身]
クライアント:
  - transfer.Write でローカルに書き込み(ハイドレート)
  - X-File-Attributes を解釈
  - File.SetAttributes でローカル NTFS の MFT に RO 属性付与

[起動時 / ツリー取得]
クライアント: GET /tree (X-Device-Id: <自分>)
サーバー: 各ノードに { isReadOnly: 他人がロック中か } を含めて返す
クライアント: CreatePlaceholders で属性付き作成

[ロック状態変化(WSS broadcast)]
サーバー: lock_acquired/lock_released を全クライアントに broadcast
クライアント: 既存ハイドレート済みファイルの属性を更新
            File.SetAttributes(path, attrs | ReadOnly) または attrs & ~ReadOnly
```

**ヘッダ仕様**:

| ヘッダ | 説明 | 例 |
|---|---|---|
| `X-File-Attributes` | カンマ区切りの属性リスト | `ReadOnly`、`Hidden,System` |
| `X-File-Lock-Holder` | ロック保持者の表示名(任意) | `alice` |

将来の拡張:

| ヘッダ | 説明 |
|---|---|
| `X-File-Permissions` | 権限ベースの RO(ADR-008) |
| `X-File-Tags` | タグ |
| `X-File-Owner` | 所有者 |

**WSS との役割分担**:

- **GET /content のヘッダ**: ハイドレート時点の状態(ボディと同時取得、原子的)
- **GET /tree の isReadOnly**: 起動時の初期状態
- **WSS lock_acquired/released**: ハイドレート後のリアルタイム変化を伝達

**SSOT 原則**:

- ロック状態 = サーバー側 KV
- ファイル属性 = サーバーが生成して送る
- クライアントは反映するだけ、独自の状態管理を持たない

**race condition の扱い**:

- A がロック取得 → サーバー broadcast → B が WSS イベント受信して RO 化 → 通常はこれで防げる
- B が先にファイルを開いてから A がロック取得 → B 側で SetAttributes が共有違反で失敗する可能性
  → このケースは編集が成立してしまうが、close 時の整合性検証で検知 → conflict file 退避(ADR-017)
- WSS 切断中 → broadcast が届かない → 復旧時に /tree で再同期(ADR-013)

**選択理由**:

- HTTP の慣習(カスタムヘッダで メタ情報伝達)に沿った自然な設計
- ボディとメタの綺麗な分離
- 1 リクエストで原子的、状態のズレが起きない
- CfApi の Hydrate フローと相性抜群(`transfer.Write` と `SetAttributes` を 1 つの流れで)
- 既存 API への変更が最小(ヘッダ追加のみ)
- 将来の拡張(タグ、所有者、権限ベース RO 等)に同じパターンで対応可能

**却下した選択肢**:

- **別 API でメタ取得**: 2 リクエスト必要、間で状態が変わる可能性
- **/tree のみ**: ハイドレート時点と /tree 取得時点でズレる
- **ファイル中身に埋め込み**: 不可能(中身は不可侵)
- **ファイルシステムレベルでサーバー側 RO**: クライアントには中身しか届かない、無意味

**関連 ADR**:

- ADR-008: 権限モデル(同じヘッダ機構で権限起因の RO も将来伝達可能)
- ADR-013: ALWAYS_FULL + WSS(/tree でも同じパターン)
- ADR-016: open 時ロック
- ADR-017: 異常事態の最終手段
- ADR-018: Device ID ベース Liveness ロック

---

## ADR-020: close 時の常時 dehydrate — VPN 越し SMB 同等の動作モデル

**決定**: ファイル close 時に常に dehydrate を試行する。ローカルキャッシュは持たず、サーバーが常に唯一の真実とする。これは **VPN 越し SMB と同じ動作モデル**であり、cafs のターゲット要件(Samba 代替)と整合する。

**動作モデルの比較**:

| 観点 | VPN 越し SMB | cafs |
|---|---|---|
| ファイル実体 | サーバー上のみ | サーバー上のみ(プレースホルダーのみローカル) |
| クライアントキャッシュ | 揮発、最終的に手放す | dehydrate で手放す |
| 通信経路 | SMB プロトコル(TCP 445) | HTTPS(CfApi + GET /content) |
| 認証 | LDAP / Kerberos | OIDC + JWT + Device ID |
| 動作の本質 | 同じ | 同じ |

**動作**:

```
[close 時]
- 編集あり: アップロード → アンロック → dehydrate 試行
- 編集なし: アンロック → dehydrate 試行

[dehydrate の挙動]
- アプリが完全にハンドル解放: 即座に成功
- アプリが中間保存等で再取得: 共有違反で失敗 → リトライキューへ
- リトライキューが定期的に再試行 → アプリ完全終了後に成功

[次回必要になった時]
- ハンドル取得 → CfApi が FETCH_DATA → GET /content で再取得
- これは SMB の「サーバーから再読み取り」と同じ動作
```

**Word 中間保存等の挙動**:

```
Word: Ctrl+S → ハンドル一瞬解放
cafs: CLOSE_COMPLETION → アップロード → dehydrate 試行
Word: 即座にハンドル再取得
cafs: dehydrate 共有違反で失敗 → リトライキューへ

または(Word が完全にハンドルを離した場合):

Word: Ctrl+S → ハンドル完全解放
cafs: CLOSE_COMPLETION → アップロード → dehydrate 成功
Word: ハンドル再取得 → CfApi: FETCH_DATA → 再ハイドレート
```

どちらの動作も VPN 越し SMB と同じ。再ハイドレートのレイテンシは HTTP/2 + 並列処理で SMB より高速化される可能性がある。

**整合性保証メカニズム**:

| 機構 | 役割 |
|---|---|
| close 時 dehydrate | 99% のケースで即座にローカル中身削除を試行 |
| dehydrate リトライキュー | 共有違反等で失敗した場合の後追い処理 |
| WSS modified イベントで再 dehydrate | 他クライアント変更時の即時対応 |
| 起動時の /tree との ETag 比較 | 全体整合性の同期、WSS 取りこぼし救済 |

これらの組み合わせで、**実用上完璧な整合性**を実現。

**CfDehydratePlaceholder の特性**:

- **ローカル NTFS 操作のみ、サーバー通信なし**
- クラスタ解放のみ、プレースホルダー(MFT のメタ情報)は残る
- コスト ≒ 0(数ms)
- 失敗ケース: pinned、他プロセス使用中、`NOT_IN_SYNC` 状態

**選択理由**:

- **VPN 越し SMB と同じ動作モデル**: cafs のターゲット要件(Samba 代替)と整合、ユーザーは慣れた挙動を得る
- **CfApi のフローと素直に整合**: 楽観的キャッシュ(ETag 確認等)は CfApi のキャッシュ判定(キャッシュあれば FETCH_DATA 呼ばない)とミスマッチ
- **整合性問題が原理的に発生しない**: ローカルに古いキャッシュが残らない
- **「サーバーが真」を厳格に守る**: 設計原則 ADR-004 と整合
- **dehydrate が無料**: ローカル操作のみ、コスト無視できる
- **セキュリティ**: ローカルに不要なデータを残さない
- **シンプル**: キャッシュ管理ロジックが不要

**Storage Sense との関係**:

Windows の Storage Sense にも「使われていないクラウドファイルを dehydrate」する機能があるが、cafs では明示的に close 時 dehydrate するため、Storage Sense の挙動には依存しない。

**Pin 機能(将来検討)**:

CfApi の `CfSetPinState` でファイルを pinned にすれば dehydrate されない。「常にローカルに置きたいファイル」をユーザーが明示的に選ぶ機能として将来追加可能。Roadmap で検討。

**却下した選択肢**:

- **楽観的キャッシュ + ETag 確認**: SMB にはない概念、CfApi のフロー(キャッシュあれば FETCH_DATA 呼ばない)と相性が悪い、open 時に介入する手段がない
- **ローカルキャッシュ活用 + Storage Sense 任せ**: cafs としての一貫性なし、整合性保証が OS 依存
- **時間ベースの dehydrate(10分後等)**: 中途半端、整合性問題の根本解決にならない

**実装上の注意**:

- close 時の `OnFileCloseAsync` の戻り値で `safeToDehydrate` を返す(現状の設計を維持)
- アップロード失敗時は `safeToDehydrate = false`(ローカル変更保護、ADR-016)
- それ以外は `safeToDehydrate = true`(編集なしでも dehydrate)
- dehydrate 失敗 → リトライキューへ(共有違反は当然ありうる、後追いで処理)
- WSS の modified イベントハンドラで「既にハイドレート済みなら dehydrate 試行」

**関連 ADR**:

- ADR-004: オフライン時の挙動(サーバーが真の原則)
- ADR-013: ALWAYS_FULL(プレースホルダーは常時残る、中身だけ dehydrate)
- ADR-014: ハイブリッド修正検出
- ADR-016: open 時ロック
- ADR-019: X-File-Attributes ヘッダ