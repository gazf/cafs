import { Hono } from "hono";
import { getStorageRoot } from "../services/file.service.ts";
import type { AuthUser } from "../services/auth.service.ts";

type Env = {
  Variables: {
    user: AuthUser;
  };
};

export function registerEventRoutes(app: Hono<Env>) {
  app.get("/events", (c) => {
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
