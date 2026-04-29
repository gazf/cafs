let kv: Deno.Kv | null = null;

export async function getKv(): Promise<Deno.Kv> {
  if (!kv) {
    kv = await Deno.openKv();
  }
  return kv;
}

export function closeKv(): void {
  if (kv) {
    kv.close();
    kv = null;
  }
}
