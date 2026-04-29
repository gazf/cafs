/**
 * /tree と /content の ADR-019 責務をテストする:
 *   - /tree: 各ノードに isReadOnly (= 他 device がロック中) を合成して返す
 *   - /content: 他 device がロック中なら X-File-Attributes: ReadOnly を付与
 *   - /content: 自 device 保持中、または無ロックなら ヘッダなし
 *
 * 注意: STORAGE_ROOT は file.service.ts の import 時に固定されるため、
 * テストは既定の `<cwd>/storage/__test_routes_files__/` 配下に実ファイルを
 * 一時生成して動かす。
 */
import { assert, assertEquals, assertFalse } from "@std/assert";
import * as path from "@std/path";
import app from "../src/app.ts";
import { acquireLock, releaseLock } from "../src/services/lock.service.ts";
import { createAppToken } from "../src/services/auth.service.ts";
import { seedUser, withTestKv } from "./_helpers.ts";

const FIXTURE_DIR = "__test_routes_files__";

async function setupFixture(): Promise<
  { root: string; cleanup: () => Promise<void> }
> {
  const root = path.join(Deno.cwd(), "storage", FIXTURE_DIR);
  await Deno.mkdir(root, { recursive: true });
  await Deno.writeTextFile(path.join(root, "shared.txt"), "shared content");
  await Deno.writeTextFile(path.join(root, "private.txt"), "private content");
  return {
    root,
    cleanup: async () => {
      try {
        await Deno.remove(root, { recursive: true });
      } catch { /* ignore */ }
    },
  };
}

interface Tokens {
  alice: string;
  bob: string;
}

async function setupUsers(kv: Deno.Kv): Promise<Tokens> {
  await seedUser(kv, {
    userId: 1,
    userName: "alice",
    groupId: 10,
    groupName: "alice-g",
    permissions: [{ path: "/", accessLevel: "write" }],
  });
  await seedUser(kv, {
    userId: 2,
    userName: "bob",
    groupId: 20,
    groupName: "bob-g",
    permissions: [{ path: "/", accessLevel: "write" }],
  });
  const a = await createAppToken(1, "alice-token");
  const b = await createAppToken(2, "bob-token");
  return { alice: a.raw, bob: b.raw };
}

function authReq(method: string, url: string, token: string, deviceId: string) {
  return new Request(url, {
    method,
    headers: {
      Authorization: `Bearer ${token}`,
      "X-Device-Id": deviceId,
    },
  });
}

interface TreeNode {
  name: string;
  path: string;
  type: string;
  size: number;
  isReadOnly: boolean;
}

Deno.test("/tree: includes isReadOnly=true for files locked by another device", async () => {
  await withTestKv(async (kv) => {
    const tokens = await setupUsers(kv);
    const fx = await setupFixture();
    try {
      // bob が shared.txt をロック
      await acquireLock(`/${FIXTURE_DIR}/shared.txt`, 2, "dev-bob-laptop");

      // alice (別 device) が /tree を取りに行く
      const res = await app.fetch(
        authReq("GET", "http://localhost/tree", tokens.alice, "dev-alice-pc"),
      );
      assertEquals(res.status, 200);
      const tree = (await res.json()) as TreeNode[];

      const shared = tree.find((n) => n.path === `/${FIXTURE_DIR}/shared.txt`);
      const priv = tree.find((n) => n.path === `/${FIXTURE_DIR}/private.txt`);
      assert(shared, "shared.txt should be in tree");
      assert(priv, "private.txt should be in tree");
      assert(shared!.isReadOnly, "shared.txt should be RO from alice's view");
      assertFalse(priv!.isReadOnly, "private.txt has no lock → not RO");
    } finally {
      await fx.cleanup();
    }
  });
});

Deno.test("/tree: isReadOnly=false for the lock holder's own device", async () => {
  await withTestKv(async (kv) => {
    const tokens = await setupUsers(kv);
    const fx = await setupFixture();
    try {
      await acquireLock(`/${FIXTURE_DIR}/shared.txt`, 1, "dev-alice-pc");

      // alice が同じ device で /tree を取得 → 自 device 保持中なので RO ではない
      const res = await app.fetch(
        authReq("GET", "http://localhost/tree", tokens.alice, "dev-alice-pc"),
      );
      const tree = (await res.json()) as TreeNode[];
      const shared = tree.find((n) => n.path === `/${FIXTURE_DIR}/shared.txt`);
      assertFalse(shared!.isReadOnly);
    } finally {
      await fx.cleanup();
    }
  });
});

Deno.test("/content: X-File-Attributes: ReadOnly when other device holds lock", async () => {
  await withTestKv(async (kv) => {
    const tokens = await setupUsers(kv);
    const fx = await setupFixture();
    try {
      await acquireLock(`/${FIXTURE_DIR}/shared.txt`, 2, "dev-bob-laptop");

      const res = await app.fetch(
        authReq(
          "GET",
          `http://localhost/content/${FIXTURE_DIR}/shared.txt`,
          tokens.alice,
          "dev-alice-pc",
        ),
      );
      assertEquals(res.status, 200);
      assertEquals(res.headers.get("X-File-Attributes"), "ReadOnly");
      // body は読み切る (リソースリーク防止)
      await res.body?.cancel();
    } finally {
      await fx.cleanup();
    }
  });
});

Deno.test("/content: no X-File-Attributes when no lock", async () => {
  await withTestKv(async (kv) => {
    const tokens = await setupUsers(kv);
    const fx = await setupFixture();
    try {
      const res = await app.fetch(
        authReq(
          "GET",
          `http://localhost/content/${FIXTURE_DIR}/private.txt`,
          tokens.alice,
          "dev-alice-pc",
        ),
      );
      assertEquals(res.status, 200);
      assertEquals(res.headers.get("X-File-Attributes"), null);
      await res.body?.cancel();
    } finally {
      await fx.cleanup();
    }
  });
});

Deno.test("/content: no X-File-Attributes when own device holds lock", async () => {
  await withTestKv(async (kv) => {
    const tokens = await setupUsers(kv);
    const fx = await setupFixture();
    try {
      await acquireLock(`/${FIXTURE_DIR}/shared.txt`, 1, "dev-alice-pc");

      const res = await app.fetch(
        authReq(
          "GET",
          `http://localhost/content/${FIXTURE_DIR}/shared.txt`,
          tokens.alice,
          "dev-alice-pc",
        ),
      );
      assertEquals(res.headers.get("X-File-Attributes"), null);
      await res.body?.cancel();

      await releaseLock(`/${FIXTURE_DIR}/shared.txt`, 1, "dev-alice-pc");
    } finally {
      await fx.cleanup();
    }
  });
});
