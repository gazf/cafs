export interface User {
  id: number;
  name: string;
  passwordHash: string;
  createdAt: string;
}

export interface Group {
  id: number;
  name: string;
}

export type AccessLevel = "read" | "write" | "admin";

export interface Permission {
  accessLevel: AccessLevel;
}

export interface TokenData {
  userId: number;
  name: string;
  expiresAt: string;
  createdAt: string;
}

export interface AuditEntry {
  userId: number;
  action: string;
  path: string;
  ip: string;
}

export interface LockData {
  userId: number;
  acquiredAt: string;
  expiresAt: string;
}

export interface FileEntry {
  name: string;
  type: "file" | "directory";
  size: number;
  lastModified: string;
}
