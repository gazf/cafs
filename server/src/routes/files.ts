import { Hono } from "hono";
import {
  deleteFile,
  FileServiceError,
  getFileInfo,
  getTree,
  listDirectory,
  readFile,
  statFile,
  writeFile,
} from "../services/file.service.ts";
import { checkPermission } from "../services/auth.service.ts";
import {
  getAllLocks,
  getLock,
  isLockedByOther,
} from "../services/lock.service.ts";
import type { AuthUser } from "../services/auth.service.ts";

type Env = {
  Variables: {
    user: AuthUser;
  };
};

export function registerFileRoutes(app: Hono<Env>) {
  // GET /tree — recursive full tree listing (read 権限のあるノードのみ返す)
  app.get("/tree", async (c) => {
    const user = c.get("user");
    try {
      const tree = await getTree();
      const locks = await getAllLocks();
      // 各ノードを read 権限でフィルタ + ADR-019 isReadOnly 合成 (他 device がロック中)。
      const checks = await Promise.all(
        tree.map(async (n) => {
          if (!(await checkPermission(user.id, n.path, "read"))) return null;
          const lock = locks.get(n.path);
          const isReadOnly = lock !== undefined &&
            lock.deviceId !== user.deviceId;
          return { ...n, isReadOnly };
        }),
      );
      const filtered = checks.filter(
        (n): n is (typeof tree)[number] & { isReadOnly: boolean } => n !== null,
      );
      return c.json(filtered);
    } catch (e) {
      if (e instanceof FileServiceError) {
        return c.json({ message: e.message }, e.statusCode as 400);
      }
      throw e;
    }
  });

  // GET /files/*path — list directory or get file info
  app.get("/files/*", async (c) => {
    const wildcard = c.req.path.replace(/^\/files\/?/, "");
    const filePath = "/" + wildcard;
    const user = c.get("user");

    if (!(await checkPermission(user.id, filePath, "read"))) {
      return c.json({ message: "Forbidden" }, 403);
    }

    try {
      const info = await getFileInfo(filePath);
      if (info.type === "directory") {
        const entries = await listDirectory(filePath);
        return c.json(entries);
      }
      return c.json(info);
    } catch (e) {
      if (e instanceof FileServiceError) {
        return c.json({ message: e.message }, e.statusCode as 400);
      }
      throw e;
    }
  });

  // DELETE /files/*path — delete file or directory
  app.delete("/files/*", async (c) => {
    const wildcard = c.req.path.replace(/^\/files\/?/, "");
    const filePath = "/" + wildcard;
    const user = c.get("user");

    if (filePath === "/" || filePath === "") {
      return c.json({ message: "Refusing to delete storage root" }, 400);
    }

    if (!(await checkPermission(user.id, filePath, "write"))) {
      return c.json({ message: "Forbidden" }, 403);
    }

    try {
      await deleteFile(filePath);
      return c.json({ message: "Deleted" }, 200);
    } catch (e) {
      if (e instanceof FileServiceError) {
        return c.json({ message: e.message }, e.statusCode as 400);
      }
      throw e;
    }
  });

  // GET /content/*path — download file content
  app.get("/content/*", async (c) => {
    const wildcard = c.req.path.replace(/^\/content\/?/, "");
    const filePath = "/" + wildcard;
    const user = c.get("user");

    if (!(await checkPermission(user.id, filePath, "read"))) {
      return c.json({ message: "Forbidden" }, 403);
    }

    try {
      // Parse Range header
      const rangeHeader = c.req.header("Range");
      let offset: number | undefined;
      let length: number | undefined;

      if (rangeHeader) {
        const match = rangeHeader.match(/bytes=(\d+)-(\d*)/);
        if (match) {
          offset = parseInt(match[1], 10);
          if (match[2]) {
            length = parseInt(match[2], 10) - offset + 1;
          }
        }
      }

      const { stream, size } = await readFile(filePath, offset, length);

      const headers: Record<string, string> = {
        "Content-Type": "application/octet-stream",
      };

      // ADR-019: 他 device が保持するロックなら ReadOnly 属性を伝達する。
      // クライアントは File.SetAttributes でローカル NTFS の MFT に反映する。
      const lock = await getLock(filePath);
      if (lock && lock.deviceId !== user.deviceId) {
        headers["X-File-Attributes"] = "ReadOnly";
      }

      if (rangeHeader && offset !== undefined) {
        const end = length ? offset + length - 1 : size - 1;
        headers["Content-Range"] = `bytes ${offset}-${end}/${size}`;
        headers["Content-Length"] = String(length ?? size - offset);
        return new Response(stream, { status: 206, headers });
      }

      headers["Content-Length"] = String(size);
      return new Response(stream, { status: 200, headers });
    } catch (e) {
      if (e instanceof FileServiceError) {
        return c.json({ message: e.message }, e.statusCode as 400);
      }
      throw e;
    }
  });

  // PUT /content/*path — upload file
  app.put("/content/*", async (c) => {
    const wildcard = c.req.path.replace(/^\/content\/?/, "");
    const filePath = "/" + wildcard;
    const user = c.get("user");

    if (!(await checkPermission(user.id, filePath, "write"))) {
      return c.json({ message: "Forbidden" }, 403);
    }

    try {
      // Check lock
      if (await isLockedByOther(filePath, user.id)) {
        return c.json({ message: "File is locked by another user" }, 423);
      }

      const body = c.req.raw.body;
      if (!body) {
        return c.json({ message: "Request body required" }, 400);
      }
      await writeFile(filePath, body);
      // Return up-to-date metadata so the client can refresh its placeholder.
      const stat = await statFile(filePath);
      return c.json(
        {
          size: stat.size,
          lastModified: stat.lastModified,
        },
        200,
      );
    } catch (e) {
      if (e instanceof FileServiceError) {
        return c.json({ message: e.message }, e.statusCode as 400);
      }
      throw e;
    }
  });
}
