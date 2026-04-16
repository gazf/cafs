import { Hono } from "hono";
import {
  listDirectory,
  getFileInfo,
  readFile,
  writeFile,
  deleteFile,
  FileServiceError,
} from "../services/file.service.ts";
import { checkPermission } from "../services/auth.service.ts";
import { isLockedByOther } from "../services/lock.service.ts";
import type { AuthUser } from "../services/auth.service.ts";

type Env = {
  Variables: {
    user: AuthUser;
  };
};

export function registerFileRoutes(app: Hono<Env>) {
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
      return c.json({ message: "OK" }, 200);
    } catch (e) {
      if (e instanceof FileServiceError) {
        return c.json({ message: e.message }, e.statusCode as 400);
      }
      throw e;
    }
  });
}
