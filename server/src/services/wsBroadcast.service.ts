import { checkPermission } from "./auth.service.ts";
import { getKv } from "../kv/store.ts";
import { Keys } from "../kv/keys.ts";
import type { User } from "../types.ts";

/**
 * ADR-018 Step 3: ロック取得・解放等のイベントを全クライアントに broadcast する。
 * 接続中の WSS ソケットを記録し、認可フィルタを掛けてから送信する。
 */

interface Peer {
  socket: WebSocket;
  userId: number;
  deviceId: string;
}

const peers = new Set<Peer>();

export function registerSocket(peer: Peer): void {
  peers.add(peer);
}

export function unregisterSocket(peer: Peer): void {
  peers.delete(peer);
}

export interface LockHolder {
  userId: number;
  deviceId: string;
  name: string;
}

async function resolveHolderName(userId: number): Promise<string> {
  const kv = await getKv();
  const user = await kv.get<User>(Keys.user(userId));
  return user.value?.name ?? `user#${userId}`;
}

export async function broadcastLockEvent(
  event: "lock_acquired" | "lock_released",
  filePath: string,
  holder: { userId: number; deviceId: string },
): Promise<void> {
  if (peers.size === 0) return;

  const name = await resolveHolderName(holder.userId);
  const payload = JSON.stringify({
    event,
    path: filePath,
    holder: { ...holder, name } satisfies LockHolder,
  });

  // 認可チェックは並列に。失敗 (権限なし) は黙って配信スキップ。
  await Promise.all(
    [...peers].map(async (peer) => {
      if (peer.socket.readyState !== WebSocket.OPEN) return;
      try {
        if (!(await checkPermission(peer.userId, filePath, "read"))) return;
        peer.socket.send(payload);
      } catch (err) {
        console.error("broadcastLockEvent send failed:", err);
      }
    }),
  );
}
