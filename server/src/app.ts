import { Hono } from "hono";
import { authMiddleware } from "./middleware/auth.ts";
import { errorHandler } from "./middleware/errors.ts";
import { registerFileRoutes } from "./routes/files.ts";
import { registerLockRoutes } from "./routes/locks.ts";
import { registerEventRoutes } from "./routes/events.ts";
import { auditLogger } from "./middleware/logger.ts";

type Env = {
  Variables: {
    user: { id: number; name: string };
  };
};

const app = new Hono<Env>();

// Global middleware
app.use("*", errorHandler);

// Health check (no auth)
app.get("/health", (c) => c.json({ status: "ok" }));

// Auth middleware for all other routes
app.use("*", authMiddleware);
app.use("*", auditLogger);

// Routes
registerFileRoutes(app);
registerLockRoutes(app);
registerEventRoutes(app);

export default app;
