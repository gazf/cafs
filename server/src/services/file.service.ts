import * as path from "https://deno.land/std@0.224.0/path/mod.ts";
import type { FileEntry } from "../types.ts";

const STORAGE_ROOT =
  Deno.env.get("CAFS_STORAGE_ROOT") ??
  path.join(Deno.cwd(), "storage");

function resolveAndValidate(relativePath: string): string {
  // Reject null bytes
  if (relativePath.includes("\0")) {
    throw new FileServiceError("Invalid path", 400);
  }

  // Strip leading slashes so path.join doesn't treat it as absolute
  const stripped = relativePath.replace(/^\/+/, "");

  // Normalize and resolve
  const normalized = stripped ? path.normalize(stripped).replace(/\\/g, "/") : ".";

  // Reject path traversal
  if (normalized.startsWith("..") || normalized.includes("/../") || normalized.endsWith("/..")) {
    throw new FileServiceError("Invalid path", 400);
  }

  const fullPath = path.join(STORAGE_ROOT, normalized);
  const resolved = path.resolve(fullPath);

  // Ensure it's within the storage root
  if (!resolved.startsWith(path.resolve(STORAGE_ROOT))) {
    throw new FileServiceError("Invalid path", 400);
  }

  return resolved;
}

export class FileServiceError extends Error {
  constructor(
    message: string,
    public statusCode: number
  ) {
    super(message);
  }
}

export async function listDirectory(
  relativePath: string
): Promise<FileEntry[]> {
  const fullPath = resolveAndValidate(relativePath);
  const entries: FileEntry[] = [];

  try {
    for await (const entry of Deno.readDir(fullPath)) {
      const stat = await Deno.stat(path.join(fullPath, entry.name));
      entries.push({
        name: entry.name,
        type: entry.isDirectory ? "directory" : "file",
        size: entry.isDirectory ? 0 : stat.size,
        lastModified: (stat.mtime ?? new Date()).toISOString(),
      });
    }
  } catch (e) {
    if (e instanceof Deno.errors.NotFound) {
      throw new FileServiceError("Not found", 404);
    }
    throw e;
  }

  return entries.sort((a, b) => {
    // Directories first, then by name
    if (a.type !== b.type) return a.type === "directory" ? -1 : 1;
    return a.name.localeCompare(b.name);
  });
}

export async function getFileInfo(
  relativePath: string
): Promise<FileEntry> {
  const fullPath = resolveAndValidate(relativePath);

  try {
    const stat = await Deno.stat(fullPath);
    const name = path.basename(fullPath);
    return {
      name,
      type: stat.isDirectory ? "directory" : "file",
      size: stat.size,
      lastModified: (stat.mtime ?? new Date()).toISOString(),
    };
  } catch (e) {
    if (e instanceof Deno.errors.NotFound) {
      throw new FileServiceError("Not found", 404);
    }
    throw e;
  }
}

export async function readFile(
  relativePath: string,
  offset?: number,
  length?: number
): Promise<{ stream: ReadableStream<Uint8Array>; size: number }> {
  const fullPath = resolveAndValidate(relativePath);

  let file: Deno.FsFile;
  try {
    file = await Deno.open(fullPath, { read: true });
  } catch (e) {
    if (e instanceof Deno.errors.NotFound) {
      throw new FileServiceError("Not found", 404);
    }
    throw e;
  }

  const stat = await file.stat();
  if (stat.isDirectory) {
    file.close();
    throw new FileServiceError("Is a directory", 400);
  }

  const totalSize = stat.size;

  if (offset !== undefined && offset > 0) {
    await file.seek(offset, Deno.SeekMode.Start);
  }

  const actualLength = length ?? totalSize - (offset ?? 0);

  let bytesRead = 0;
  const stream = new ReadableStream<Uint8Array>({
    async pull(controller) {
      const remaining = actualLength - bytesRead;
      if (remaining <= 0) {
        controller.close();
        file.close();
        return;
      }

      const chunkSize = Math.min(65536, remaining);
      const buf = new Uint8Array(chunkSize);
      const n = await file.read(buf);
      if (n === null || n === 0) {
        controller.close();
        file.close();
        return;
      }

      bytesRead += n;
      controller.enqueue(buf.subarray(0, n));
    },
    cancel() {
      file.close();
    },
  });

  return { stream, size: totalSize };
}

export async function writeFile(
  relativePath: string,
  body: ReadableStream<Uint8Array>
): Promise<void> {
  const fullPath = resolveAndValidate(relativePath);

  // Ensure parent directory exists
  const dir = path.dirname(fullPath);
  await Deno.mkdir(dir, { recursive: true });

  const file = await Deno.open(fullPath, {
    write: true,
    create: true,
    truncate: true,
  });

  try {
    const reader = body.getReader();
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      await writeAll(file, value);
    }
  } finally {
    file.close();
  }
}

async function writeAll(file: Deno.FsFile, data: Uint8Array): Promise<void> {
  let written = 0;
  while (written < data.length) {
    written += await file.write(data.subarray(written));
  }
}

export async function deleteFile(relativePath: string): Promise<void> {
  const fullPath = resolveAndValidate(relativePath);

  try {
    const stat = await Deno.stat(fullPath);
    await Deno.remove(fullPath, { recursive: stat.isDirectory });
  } catch (e) {
    if (e instanceof Deno.errors.NotFound) {
      throw new FileServiceError("Not found", 404);
    }
    throw e;
  }
}

export function getStorageRoot(): string {
  return STORAGE_ROOT;
}

export interface TreeEntry extends FileEntry {
  path: string;
}

export async function getTree(): Promise<TreeEntry[]> {
  const results: TreeEntry[] = [];

  async function walk(relPath: string): Promise<void> {
    const entries = await listDirectory(relPath);
    for (const entry of entries) {
      const entryPath = relPath === "/" ? `/${entry.name}` : `${relPath}/${entry.name}`;
      results.push({ ...entry, path: entryPath });
      if (entry.type === "directory") {
        await walk(entryPath);
      }
    }
  }

  await walk("/");
  return results;
}
