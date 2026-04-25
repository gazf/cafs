# Architecture Decision Records

実装に大きな影響を与える設計判断を、決定事項・選択肢・選択理由を残す形で記録する。

## ADR-013: プレースホルダーポリシー — ALWAYS_FULL + WSS イベント駆動

### 決定

起動時に `/tree` で全ツリーを取得し、`CF_POPULATION_POLICY_ALWAYS_FULL` でプレースホルダーを一括作成する。
以降の差分は WSS (`/events`) で受け取り、`SyncEngine.HandleEvent` でローカルプレースホルダーに反映する。

### 却下した選択肢

- **PARTIAL + FETCH_PLACEHOLDERS (オンデマンド列挙)**: ディレクトリ展開時に毎回コールバックを発火するため、
  サーバ往復のレイテンシが UI を待たせる。実機検証で UX が著しく劣化することを確認した。

### 選択理由

- エクスプローラー操作が即座に応答する (オフラインファイルと同じ感覚)
- 他クライアントの変更が WSS でリアルタイムに反映される
- 起動時に全ツリーを舐めるコストはあるが、実機検証で許容範囲と判断

### 実装上の知見 (実機検証由来)

- ALWAYS_FULL でも OS が `FETCH_PLACEHOLDERS` を送るケースがあるため、空リスト即答ハンドラーは残す
  (commit 6d9ea7f)
- 再起動時 `CfCreatePlaceholders` が `ERROR_ALREADY_EXISTS` (0x800700B7) を返すのは正常動作
  (commit 767c3d0)
- WebSocket 切断時は 5 秒バックオフで自動再接続 (commit 6d9ea7f)
- `CfCreatePlaceholders` / `File.Delete` だけでは Explorer のビューが自動更新されないため、
  `SHChangeNotify` を併用する (issue #3)

### スケーラビリティの制約

- 起動時の `/tree` 取得と `CreatePlaceholders` のコストが線形に効くため、
  現実的な上限は 10 万ファイル程度と想定
- それを超える場合は、ルート直下のみ ALWAYS_FULL でその下は PARTIAL という
  ハイブリッド戦略への移行を検討する

### フォールバック

- WSS 切断時は 5 秒バックオフで自動再接続
- イベント取りこぼし時は `OnSyncNow` (フルツリー再取得) で回復
- 将来的には Last-Event-ID ベースの取りこぼし検出を実装 (別 Issue 化予定)

### 関連コミット

- `9c2fac5` feat: CF_POPULATION_POLICY_ALWAYS_FULL + WSS イベント駆動に移行
- `f878abf` server: WebSocket イベントストリームの二重クローズ修正
- `767c3d0` CfApi.Interop: ERROR_ALREADY_EXISTS を正常として扱う
- `6d9ea7f` client: FETCH_PLACEHOLDERS 即答ハンドラーと WebSocket 自動再接続を追加
