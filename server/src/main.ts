import app from "./app.ts";
import { initFileLogger } from "./util/fileLogger.ts";

initFileLogger();

const port = parseInt(Deno.env.get("CAFS_PORT") ?? "8700", 10);

console.log(`cafs server starting on port ${port}`);

Deno.serve({ port }, app.fetch);
