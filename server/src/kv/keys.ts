/**
 * Deno KV キー設計
 *
 * ["users", id]                      → User
 * ["users_by_name", name]            → id (セカンダリインデックス)
 * ["groups", id]                     → Group
 * ["user_groups", userId, groupId]   → true
 * ["permissions", path, groupId]     → { accessLevel }
 * ["tokens", tokenHash]              → TokenData
 * ["tokens_by_user", userId, tokenHash] → true
 * ["audit", timestamp, id]           → AuditEntry
 * ["locks", path]                    → LockData
 * ["counters", entity]               → number (auto-increment)
 */

export const Keys = {
  user: (id: number): Deno.KvKey => ["users", id],
  userByName: (name: string): Deno.KvKey => ["users_by_name", name],
  group: (id: number): Deno.KvKey => ["groups", id],
  userGroup: (userId: number, groupId: number): Deno.KvKey => [
    "user_groups",
    userId,
    groupId,
  ],
  userGroupsPrefix: (userId: number): Deno.KvKey => ["user_groups", userId],
  permission: (path: string, groupId: number): Deno.KvKey => [
    "permissions",
    path,
    groupId,
  ],
  permissionsPrefix: (path: string): Deno.KvKey => ["permissions", path],
  token: (tokenHash: string): Deno.KvKey => ["tokens", tokenHash],
  tokenByUser: (userId: number, tokenHash: string): Deno.KvKey => [
    "tokens_by_user",
    userId,
    tokenHash,
  ],
  tokensByUserPrefix: (userId: number): Deno.KvKey => [
    "tokens_by_user",
    userId,
  ],
  audit: (timestamp: string, id: number): Deno.KvKey => [
    "audit",
    timestamp,
    id,
  ],
  auditPrefix: (): Deno.KvKey => ["audit"],
  lock: (path: string): Deno.KvKey => ["locks", path],
  counter: (entity: string): Deno.KvKey => ["counters", entity],
} as const;
