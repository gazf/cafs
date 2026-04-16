import { getKv } from "../kv/store.ts";
import { Keys } from "../kv/keys.ts";
import type { LockData } from "../types.ts";

const DEFAULT_LOCK_TIMEOUT_MS = 15 * 60 * 1000; // 15 minutes

export interface LockResult {
  success: boolean;
  lock?: LockData;
  message?: string;
}

export async function acquireLock(
  filePath: string,
  userId: number,
  timeoutMs: number = DEFAULT_LOCK_TIMEOUT_MS
): Promise<LockResult> {
  const kv = await getKv();
  const key = Keys.lock(filePath);

  const existing = await kv.get<LockData>(key);

  // Check if already locked by someone else
  if (existing.value) {
    // Check if expired
    if (new Date(existing.value.expiresAt) > new Date()) {
      if (existing.value.userId === userId) {
        // Renew lock
        const lock: LockData = {
          userId,
          acquiredAt: existing.value.acquiredAt,
          expiresAt: new Date(Date.now() + timeoutMs).toISOString(),
        };
        const result = await kv
          .atomic()
          .check(existing)
          .set(key, lock)
          .commit();
        if (result.ok) {
          return { success: true, lock };
        }
        return { success: false, message: "Conflict" };
      }
      return {
        success: false,
        lock: existing.value,
        message: "Locked by another user",
      };
    }
    // Lock expired, take over
  }

  const lock: LockData = {
    userId,
    acquiredAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + timeoutMs).toISOString(),
  };

  // Atomic: only set if the value hasn't changed
  const result = await kv.atomic().check(existing).set(key, lock).commit();

  if (!result.ok) {
    return { success: false, message: "Conflict" };
  }

  return { success: true, lock };
}

export async function releaseLock(
  filePath: string,
  userId: number
): Promise<boolean> {
  const kv = await getKv();
  const key = Keys.lock(filePath);
  const existing = await kv.get<LockData>(key);

  if (!existing.value) return true; // Already unlocked

  if (existing.value.userId !== userId) {
    return false; // Not the holder
  }

  const result = await kv.atomic().check(existing).delete(key).commit();
  return result.ok;
}

export async function getLock(filePath: string): Promise<LockData | null> {
  const kv = await getKv();
  const entry = await kv.get<LockData>(Keys.lock(filePath));
  if (!entry.value) return null;

  // Check if expired
  if (new Date(entry.value.expiresAt) <= new Date()) {
    // Clean up expired lock
    await kv.atomic().check(entry).delete(Keys.lock(filePath)).commit();
    return null;
  }

  return entry.value;
}

export async function isLockedByOther(
  filePath: string,
  userId: number
): Promise<boolean> {
  const lock = await getLock(filePath);
  return lock !== null && lock.userId !== userId;
}
