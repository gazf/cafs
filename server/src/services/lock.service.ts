import { getKv } from "../kv/store.ts";
import { Keys } from "../kv/keys.ts";
import type { LockData } from "../types.ts";

// ADR-018: Liveness 管理は WSS heartbeat による KV expireIn の延長で行う。
// 30 秒は heartbeat 10 秒間隔 × 3 回分の猶予 (一時的なネット断 2 回まで耐える)。
const LOCK_TTL_MS = 30 * 1000;

export interface LockResult {
  success: boolean;
  lock?: LockData;
  message?: string;
}

export async function acquireLock(
  filePath: string,
  userId: number,
  deviceId: string,
  timeoutMs: number = LOCK_TTL_MS
): Promise<LockResult> {
  const kv = await getKv();
  const key = Keys.lock(filePath);

  const existing = await kv.get<LockData>(key);

  if (existing.value) {
    if (existing.value.userId !== userId) {
      // 他ユーザー保持中 → 拒否
      return {
        success: false,
        lock: existing.value,
        message: "Locked by another user",
      };
    }
    // 同一ユーザー: 同じ deviceId なら renew、別 deviceId なら取り戻し (ADR-018)
  }

  const now = new Date();
  const lock: LockData = {
    userId,
    deviceId,
    acquiredAt: existing.value?.acquiredAt ?? now.toISOString(),
    expiresAt: new Date(now.getTime() + timeoutMs).toISOString(),
  };

  // 取り戻しの場合は古い deviceId の逆引きインデックスも削除する。
  const tx = kv.atomic().check(existing);
  if (existing.value && existing.value.deviceId !== deviceId) {
    tx.delete(Keys.deviceLock(existing.value.deviceId, filePath));
  }

  const result = await tx
    .set(key, lock, { expireIn: timeoutMs })
    .set(Keys.deviceLock(deviceId, filePath), null, { expireIn: timeoutMs })
    .commit();

  if (!result.ok) {
    return { success: false, message: "Conflict" };
  }

  return { success: true, lock };
}

export async function releaseLock(
  filePath: string,
  userId: number,
  deviceId: string
): Promise<boolean> {
  const kv = await getKv();
  const key = Keys.lock(filePath);
  const existing = await kv.get<LockData>(key);

  if (!existing.value) return true; // Already unlocked

  if (existing.value.userId !== userId) {
    return false; // 他ユーザー保持中: 解除不可
  }

  // 同一ユーザーなら deviceId が異なっても解除を許す (取り戻し中に旧端末が
  // close した時に現端末のロックを誤って消さないよう、deviceId 一致時のみ
  // 逆引きインデックスも削除する)。
  const tx = kv.atomic().check(existing).delete(key);
  if (existing.value.deviceId === deviceId) {
    tx.delete(Keys.deviceLock(deviceId, filePath));
  }

  const result = await tx.commit();
  return result.ok;
}

export async function getLock(filePath: string): Promise<LockData | null> {
  const kv = await getKv();
  const entry = await kv.get<LockData>(Keys.lock(filePath));
  // KV expireIn により満期判定は不要。値があれば有効。
  return entry.value ?? null;
}

export async function isLockedByOther(
  filePath: string,
  userId: number
): Promise<boolean> {
  const lock = await getLock(filePath);
  return lock !== null && lock.userId !== userId;
}
