import path from "node:path";
import { mkdir, rename, writeFile } from "node:fs/promises";

/** Replace private JSON state atomically. Callers serialize writes to the same destination. */
export async function writeAtomicJson(filePath: string, value: unknown): Promise<void> {
  await mkdir(path.dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.tmp`;
  await writeFile(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, { mode: 0o600 });
  await rename(temporaryPath, filePath);
}
