import { Hono } from "hono";
import { getStorageRoot } from "../services/file.service.ts";
import { checkPermission } from "../services/auth.service.ts";
import type { AuthUser } from "../services/auth.service.ts";

type Env = {
  Variables: {
    user: AuthUser;
  };
};

export function registerEventRoutes(app: Hono<Env>) {
  app.get("/events", (c) => {
    // 接続ユーザーを auth ミドルウェアから取得し、配信前に read 権限を確認する。
    const user = c.get("user");
    const { socket, response } = Deno.upgradeWebSocket(c.req.raw);
    const root = getStorageRoot();
    const watcher = Deno.watchFs(root, { recursive: true });

    socket.onopen = () => {
      (async () => {
        try {
          for await (const event of watcher) {
            if (socket.readyState !== WebSocket.OPEN) break;

            for (const p of event.paths) {
              const rel = "/" + p.slice(root.length).replace(/\\/g, "/").replace(/^\/+/, "");
              if (!rel || rel === "/") continue;

              // 認可フィルタ: read 権限がなければ配信しない (パス・サイズ・更新時刻の漏洩を防ぐ)。
              if (!(await checkPermission(user.id, rel, "read"))) continue;

              if (event.kind === "create" || event.kind === "modify") {
                try {
                  const stat = await Deno.stat(p);
                  socket.send(JSON.stringify({
                    event: event.kind === "create" ? "created" : "modified",
                    path: rel,
                    type: stat.isDirectory ? "directory" : "file",
                    size: stat.isDirectory ? 0 : stat.size,
                    lastModified: (stat.mtime ?? new Date()).toISOString(),
                  }));
                } catch {
                  // File already deleted between event and stat — skip
                }
              } else if (event.kind === "remove") {
                socket.send(JSON.stringify({ event: "deleted", path: rel }));
              }
            }
          }
        } catch {
          // watcher already closed by onclose/onerror — ignore
        }
      })();
    };

    const closeWatcher = () => { try { watcher.close(); } catch { /* already closed */ } };
    socket.onclose = closeWatcher;
    socket.onerror = closeWatcher;

    return response;
  });
}
