import { Hono } from "hono";
import { getStorageRoot } from "../services/file.service.ts";
import { checkPermission } from "../services/auth.service.ts";
import { refreshDeviceLocks } from "../services/lock.service.ts";
import type { AuthUser } from "../services/auth.service.ts";

type Env = {
  Variables: {
    user: AuthUser;
  };
};

interface IncomingMessage {
  type?: string;
  deviceId?: string;
}

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

    // ADR-018 Step 2: WSS heartbeat。クライアントが 10 秒間隔で送る {type:"heartbeat", deviceId}
    // を受け取り、当該 device の全ロック (device_locks 逆引き) の TTL を 30 秒延長する。
    // deviceId は接続時に検証済みの user.deviceId と一致する場合のみ受理 (なりすまし防止)。
    socket.onmessage = (ev) => {
      let msg: IncomingMessage;
      try {
        msg = JSON.parse(typeof ev.data === "string" ? ev.data : "");
      } catch {
        return;
      }

      if (msg.type === "heartbeat") {
        if (msg.deviceId !== user.deviceId) return;
        refreshDeviceLocks(user.deviceId).catch((err) => {
          console.error("refreshDeviceLocks failed:", err);
        });
      }
    };

    const closeWatcher = () => { try { watcher.close(); } catch { /* already closed */ } };
    socket.onclose = closeWatcher;
    socket.onerror = closeWatcher;

    return response;
  });
}
