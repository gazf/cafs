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

**決定(ADR-005 を更新)**: ファイル open 時にサーバー側ロックを取得し、SID(Session ID)+ WSS ハートビートで Liveness ベースの管理を行う。詳細は ADR-018 を参照。

**経緯**:

ADR-005 で「open 時にロック取得」を決定 → Phase 5 で「読み取り目的のオープンでもロック発生」が懸念点として浮上 → 一時的に「close 時のみロック」(楽観的方式)に変更 → サーバー負荷の懸念は 100 ユーザー規模では問題ないと判明 → Samba ライクな UX を取り戻すため、SID + WSS ハートビートで再設計。

**100 人規模での負荷試算**:

- ロック取得 1 回 ≈ 5ms(JWT検証 + checkPermission + KV.get + KV.atomic)
- Deno KV スループット ≈ 数千 ops/秒
- ピーク 50 ops/秒(100 ユーザー × 5 ファイル × 10 秒集中)
- 利用率 ≈ 13%、余裕

**Samba との UX 整合性**:

- 編集前に「他ユーザーが編集中」の警告が出る(ADR-018 で実装)
- 異常終了時のロック残存は WSS 切断 + KV TTL で 30 秒以内に自動解除(ADR-018)

**保険機構**:

- ロック取得失敗時(他ユーザー編集中)はローカル変更を `<n>.conflict-<timestamp><ext>` として退避(ADR-017)
- データは絶対に失わない

**関連 ADR**:

- ADR-005: 旧仕様(close 時のみロック)を本 ADR で更新
- ADR-017: コンフリクトファイル戦略
- ADR-018: SID ベース Liveness ロック

---

## ADR-017: コンフリクトファイル戦略

**決定**: ロック取得失敗(他ユーザー編集中)時、ローカルの変更を `<stem>.conflict-<yyyyMMdd-HHmmss><ext>` として保存し、元ファイルは dehydrate して**サーバー版**に巻き戻す。

**背景**:

ロック取得失敗 = 他ユーザーが既に編集中。ローカル変更とサーバー変更が両立する状況。

**選択肢の比較**:

- **ローカル変更を破棄**: ユーザーの作業が失われる、致命的
- **アップロードを強行**: サーバー側のロックで拒否されるはずだが、整合性の保証が下がる
- **コンフリクトファイルとして退避**: ユーザーの作業は守られ、両者の変更が残る ✅

**選択理由**:

- ユーザーの作業を絶対に失わない(データロスゼロ原則)
- Dropbox / OneDrive と同じ方式(エンドユーザーが慣れている)
- 後からマージ作業が可能
- 元ファイルがサーバー版に同期されるので整合性が保たれる

**実装**:

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

    return true; // 元ファイルは dehydrate して server 版に戻す
}
```

**ファイル名の例**:

```
report.docx を編集中、ロック取得失敗 →
  report.conflict-20260425-143022.docx に退避
  report.docx は dehydrate でサーバー最新に戻る
```

**関連 ADR**:

- ADR-016: ロック戦略
- ADR-018: SID ベース Liveness ロック

---

## ADR-018: SID ベース Liveness ロック管理

**決定**: クライアント端末ごとに Session ID(SID, UUID v4)を生成し、ロックを `(user.id, sid)` で識別する。WSS ハートビートで TTL 延長、異常時は Deno KV の `expireIn` で自動解除、グレースフル終了時は SID 単位で一括解除する。

**動作モデル**:

```
1. アプリ起動時に SID 生成(UUID v4、プロセス内のみで保持)
2. ファイル open → HTTP POST /locks/* (X-Session-Id ヘッダ付き)
   サーバー側: Deno KV に保存(expireIn: 30s, sessionId: sid)
3. WSS 経由で 10 秒ごとにハートビート: { type: "heartbeat", sid }
   サーバー側: SID で逆引き → 全ロックの expireIn を 30 秒延長
4. 異常終了:
   - WSS 切断検知 → サーバー側で SID の全ロック解除(任意)
   - または KV の expireIn(30 秒)で自動削除
5. グレースフル終了:
   - WSS で { type: "terminate", sid } 送信
   - サーバー側で SID 単位の即時解除
```

**パラメータ**:

- ハートビート間隔: 10 秒
- TTL: 30 秒(3 回分の猶予 → 一時的なネット断 2 回まで耐える)
- SID 形式: UUID v4(122 ビットエントロピー、推測不可能)

**選択理由**:

### Samba を超える UX

| 観点 | Samba | 純 TTL 方式 | SID ベース |
|---|---|---|---|
| 異常終了時の解除 | TCP 切断で即時 | 15分待ち | 30秒以内 ✅ |
| ロック識別 | プロセス | userId のみ | (userId, sid) ✅ |
| グレースフル終了 | 自動 | TTL 待ち | 即時 ✅ |
| ネット断耐性 | 弱 | 強 | 強 ✅ |

### 実装上の優位性

- **ハートビートペイロード = SID のみ**(数十バイト固定)。「持っているロック一覧」を送らないので、ロック数によらずスケーラブル
- **状態は完全にサーバー側**(Single Source of Truth)。クライアントは SID だけ持つ
- **Deno KV の `expireIn` を活用**。サーバー側のクリーンアップループ不要
- **既存 WSS 接続を活用**。新規接続を増やさない

### 同一ユーザー別端末の扱い

`(user.id, sid)` で識別するが、**同一 user.id なら新 SID で取り戻せる**:

```typescript
async function acquireLock(filePath, userId, sid) {
    const existing = await kv.get<LockData>(Keys.lock(filePath));
    
    if (existing.value?.userId === userId) {
        // 同一ユーザー → SID を付け替えて renew
        const updated = { ...existing.value, sessionId: sid, ... };
        await kv.set(key, updated, { expireIn: LOCK_TTL_MS });
        return { success: true };
    }
    if (existing.value && existing.value.userId !== userId) {
        return { success: false }; // 他ユーザー保持中
    }
    // 新規取得
}
```

これで「PC で開いたファイルをノートで取り戻す」が可能。OneDrive と同じ方式。

**却下した選択肢**:

- **「持っているロック一覧」をハートビートで送信**: ペイロード過大、状態同期が複雑、SSOT 原則に反する
- **純 TTL のみ(SID なし)**: 異常時 30 秒待ちは避けられない、グレースフル解除も userId ベースで雑
- **HTTP 経由ハートビート**: WSS の双方向性を活かせない、コネクション数増

**段階的な導入**:

1. **Step 1**: SID + HTTP ヘッダ + TTL 30 秒短縮(2〜4 時間)
2. **Step 2**: WSS ハートビート化(2〜4 時間)
3. **Step 3**: グレースフル `terminate` 実装(2〜4 時間)

合計 6〜12 時間程度で完成可能。

**関連 ADR**:

- ADR-005: コンフリクト解決(本 ADR で具体化)
- ADR-016: ロック取得タイミング(本 ADR の前提)
- ADR-017: コンフリクトファイル戦略(失敗時の保険)

**関連 Issue**:

- 既存 Issue 7(ハートビート): 本 ADR で具体化、SID 方式に変更
- 新規: ADR-018 実装(別 Issue として起票推奨)

---

## 判断基準(新しい決定をする時)

以下の 3 つの問いで判定:

1. **これは VPN + Samba + LDAP からの脱却に貢献するか?**
2. **これは Zero Trust の原則に沿っているか?**
3. **これはモダンな技術らしい、シンプルで透明な実装か?**

すべて Yes なら採用、どれか No なら再検討。
