let kv: Deno.Kv | null = null;

export async function getKv(): Promise<Deno.Kv> {
  if (!kv) {
    kv = await Deno.openKv();
  }
  return kv;
}

export async function closeKv(): Promise<void> {
  if (kv) {
    kv.close();
    kv = null;
  }
}
