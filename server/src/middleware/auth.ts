import { createMiddleware } from "@hono/hono/factory";
import { validateToken, type AuthUser } from "../services/auth.service.ts";

type Env = {
  Variables: {
    user: AuthUser;
  };
};

export const authMiddleware = createMiddleware<Env>(async (c, next) => {
  // Skip auth for health check
  if (c.req.path === "/health") {
    await next();
    return;
  }

  const authHeader = c.req.header("Authorization");
  if (!authHeader || !authHeader.startsWith("Bearer ")) {
    return c.json({ message: "Missing or invalid Authorization header" }, 401);
  }

  const token = authHeader.slice(7);
  const user = await validateToken(token);
  if (!user) {
    return c.json({ message: "Invalid or expired token" }, 401);
  }

  c.set("user", user);
  await next();
});
