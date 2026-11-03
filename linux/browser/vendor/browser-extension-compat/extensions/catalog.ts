import path from "node:path";
import { access, readFile } from "node:fs/promises";
import { writeAtomicJson } from "./atomic-json.js";
import { z } from "zod";
import log from "electron-log";
import { ExtensionHostCrashError } from "./runtime-failure.js";

const EntrySchema = z.object({
  id: z.string().regex(/^[a-p]{32}$/),
  name: z.string(),
  version: z.string(),
  path: z.string(),
  source: z.enum(["store", "unpacked"]),
  enabled: z.boolean(),
  pinned: z.boolean().optional(),
});

export type BrowserExtensionEntry = z.infer<typeof EntrySchema>;
export type LoadedBrowserExtension = Pick<
  BrowserExtensionEntry,
  "id" | "name" | "version" | "path"
>;

export interface BrowserExtensionRuntime {
  isLoaded(id: string): boolean;
  load(extensionPath: string): Promise<LoadedBrowserExtension>;
  unload(id: string): void;
  uninstall(id: string): Promise<void>;
}

const RECOVERY_DELAYS_MS = [1_000, 5_000, 15_000];
const RECOVERY_WINDOW_MS = 10 * 60_000;

interface CrashRecovery {
  id: string;
  crash: ExtensionHostCrashError;
  startedAt: number;
  attempts: number;
  status: "idle" | "queued" | "loading";
  exhausted: boolean;
  timer: ReturnType<typeof setTimeout> | undefined;
}

type RecoveryEvent =
  | "scheduled"
  | "attempt"
  | "ready"
  | "failed"
  | "cancelled"
  | "exhausted"
  | "already-ready";

/** Classify managed Store folders consistently for catalog display and packaged-extension API policy. */
export function getBrowserExtensionSource(
  storePath: string,
  extensionPath: string,
): BrowserExtensionEntry["source"] {
  const relativePath = path.relative(storePath, extensionPath);
  if (relativePath && !relativePath.startsWith("..") && !path.isAbsolute(relativePath)) {
    return "store";
  }
  return "unpacked";
}

/** Owns saved extension choices; disabled extensions never execute during startup. */
export class BrowserExtensionCatalog {
  private entries: BrowserExtensionEntry[] = [];
  private readonly errors = new Map<string, Error>();
  private queue: Promise<unknown> = Promise.resolve();
  private readonly recoveries = new Map<string, CrashRecovery>();
  private disposed = false;

  public constructor(
    private readonly filePath: string,
    private readonly storePath: string,
    private readonly runtime: BrowserExtensionRuntime,
    private readonly onChanged: () => void = () => {},
  ) {}

  /** Restore each enabled extension independently so one broken folder cannot block the browser. */
  public async restore(): Promise<void> {
    try {
      this.entries = z.array(EntrySchema).parse(JSON.parse(await readFile(this.filePath, "utf8")));
    } catch (error) {
      if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) {
        throw error;
      }
    }

    for (const entry of this.entries) {
      if (!entry.enabled) {
        continue;
      }
      try {
        const loaded = await this.runtime.load(entry.path);
        if (loaded.id !== entry.id) {
          this.runtime.unload(loaded.id);
          throw new Error(
            "The extension folder now contains a different extension. Remove and add it again.",
          );
        }
        Object.assign(entry, loaded);
      } catch (error) {
        this.errors.set(entry.id, error instanceof Error ? error : new Error(String(error)));
      }
    }
  }

  /** Wait for pending installs and return snapshots including recoverable load errors. */
  public async list(): Promise<Array<BrowserExtensionEntry & { error?: string }>> {
    await this.serialize(() => this.pruneStoreRemovals());
    return this.entries.map((entry) => ({ ...entry, error: this.errors.get(entry.id)?.message }));
  }

  /** Store-page removals bypass settings. Forget only files removed from a previously working install. */
  private async pruneStoreRemovals(): Promise<void> {
    const removedIds = new Set<string>();
    for (const entry of this.entries) {
      if (
        entry.source !== "store" ||
        !entry.enabled ||
        this.errors.has(entry.id) ||
        this.runtime.isLoaded(entry.id)
      ) {
        continue;
      }
      try {
        await access(entry.path);
      } catch (error) {
        if (error instanceof Error && "code" in error && error.code === "ENOENT") {
          removedIds.add(entry.id);
        } else {
          throw error;
        }
      }
    }
    if (removedIds.size > 0) {
      await this.save(this.entries.filter((entry) => !removedIds.has(entry.id)));
    }
  }

  /** Track Web Store installs and unpacked loads in the same persistent catalog. */
  public remember(extension: LoadedBrowserExtension): Promise<void> {
    return this.serialize(async () => {
      const priorError = this.errors.get(extension.id);
      const source = getBrowserExtensionSource(this.storePath, extension.path);
      const previous = this.entries.find((entry) => entry.id === extension.id);
      const next = this.entries.filter((entry) => entry.id !== extension.id);
      next.push({
        ...extension,
        source,
        enabled: true,
        ...(previous?.pinned === undefined ? {} : { pinned: previous.pinned }),
      });
      await this.save(next);
      this.clearUnchangedError(extension.id, priorError);
    });
  }

  /** Persist disable before unloading; roll back a newly loaded extension if saving enable fails. */
  public setEnabled(id: string, enabled: boolean): Promise<void> {
    // Cancel intent immediately, including a recovery already queued behind another catalog mutation.
    this.cancelRecovery(id);
    return this.serialize(async () => {
      const entry = this.requireEntry(id);
      const priorError = this.errors.get(id);
      if (entry.enabled === enabled && this.runtime.isLoaded(id) === enabled) {
        this.errors.delete(id);
        return;
      }
      if (enabled) {
        const wasLoaded = this.runtime.isLoaded(id);
        const loaded = await this.runtime.load(entry.path);
        if (loaded.id !== id) {
          this.runtime.unload(loaded.id);
          throw new Error("The extension identity changed. Remove and add it again.");
        }
        try {
          await this.save(
            this.entries.map((item) => (item.id === id ? { ...item, ...loaded, enabled } : item)),
          );
        } catch (error) {
          if (!wasLoaded) {
            this.runtime.unload(id);
          }
          throw error;
        }
      } else {
        await this.save(this.entries.map((item) => (item.id === id ? { ...item, enabled } : item)));
        this.runtime.unload(id);
      }
      this.clearUnchangedError(id, priorError);
    });
  }

  /** A completed save can clear an earlier error, never a newer asynchronous runtime failure. */
  private clearUnchangedError(id: string, priorError: Error | undefined): void {
    if (this.errors.get(id) === priorError) this.errors.delete(id);
  }

  /** Publish a runtime failure without changing the user's durable enabled choice. */
  public recordRuntimeFailure(id: string, error: Error): void {
    this.errors.set(id, error);
    const entry = this.entries.find((item) => item.id === id);
    if (error instanceof ExtensionHostCrashError && entry) {
      log.error("[browser-extensions] host crashed", { extensionId: id, ...error.diagnostic });
      if (!this.disposed && entry.enabled) this.queueCrashRecovery(id, error);
    } else {
      // A later non-crash failure is not evidence that this generation should be restarted.
      const recovery = this.recoveries.get(id);
      if (recovery && recovery.status !== "loading") this.cancelRecovery(id);
    }
    this.onChanged();
  }

  /** Keep the budget across successful reloads; only an elapsed window or explicit user action resets it. */
  private queueCrashRecovery(id: string, crash: ExtensionHostCrashError): void {
    let recovery = this.recoveries.get(id);
    if (
      recovery &&
      recovery.status === "idle" &&
      Date.now() - recovery.startedAt >= RECOVERY_WINDOW_MS
    ) {
      this.cancelRecovery(id);
      recovery = undefined;
    }
    if (!recovery) {
      recovery = {
        id,
        crash,
        startedAt: Date.now(),
        attempts: 0,
        status: "idle",
        exhausted: false,
        timer: undefined,
      };
      this.recoveries.set(id, recovery);
    }
    recovery.crash = crash;
    this.scheduleRecovery(recovery);
  }

  /** Coalesce repeated failure events and join retries to the existing catalog mutation queue. */
  private scheduleRecovery(recovery: CrashRecovery): void {
    if (
      !this.isCurrentRecovery(recovery) ||
      recovery.status !== "idle" ||
      recovery.timer !== undefined
    )
      return;
    const delayMs = RECOVERY_DELAYS_MS[recovery.attempts];
    if (delayMs === undefined) {
      if (!recovery.exhausted) this.logRecovery(recovery, "exhausted");
      recovery.exhausted = true;
      return;
    }
    this.logRecovery(recovery, "scheduled", { delayMs });
    recovery.timer = setTimeout(() => {
      recovery.timer = undefined;
      recovery.status = "queued";
      void this.serialize(() => this.recoverCrashedExtension(recovery)).catch((error) => {
        this.logRecovery(recovery, "failed", { error });
      });
    }, delayMs);
    recovery.timer.unref();
  }

  /** Never reload a disabled, removed, superseded or shutting-down extension. */
  private isCurrentRecovery(recovery: CrashRecovery): boolean {
    return (
      !this.disposed &&
      this.recoveries.get(recovery.id) === recovery &&
      this.entries.some((entry) => entry.id === recovery.id && entry.enabled)
    );
  }

  /** Use the same native load/readiness gate as manual enable; saved choices and profile data are untouched. */
  private async recoverCrashedExtension(recovery: CrashRecovery): Promise<void> {
    let failed = false;
    let retryable = true;
    try {
      if (!this.isCurrentRecovery(recovery)) return;
      const entry = this.requireEntry(recovery.id);
      if (this.runtime.isLoaded(entry.id)) {
        this.clearUnchangedError(entry.id, recovery.crash);
        this.onChanged();
        this.logRecovery(recovery, "already-ready");
        return;
      }
      recovery.attempts++;
      this.logRecovery(recovery, "attempt");
      const priorError = this.errors.get(entry.id);
      recovery.status = "loading";
      // Fail closed if native disposal fails; never load over an incompletely retired host.
      this.runtime.unload(entry.id);
      const loaded = await this.runtime.load(entry.path);
      if (loaded.id !== entry.id) {
        retryable = false;
        this.runtime.unload(loaded.id);
        throw new Error("The extension identity changed. Remove and add it again.");
      }
      if (!this.isCurrentRecovery(recovery)) {
        this.runtime.unload(loaded.id);
        this.logRecovery(recovery, "cancelled");
        return;
      }
      // A ready callback followed by another crash must not be published as successful recovery.
      const newerFailure = this.errors.get(entry.id) !== priorError;
      if (newerFailure || !this.runtime.isLoaded(entry.id)) {
        throw this.errors.get(entry.id) ?? new Error("Extension recovery did not become ready.");
      }
      Object.assign(entry, loaded);
      this.clearUnchangedError(entry.id, priorError);
      this.onChanged();
      this.logRecovery(recovery, "ready");
    } catch (error) {
      if (this.isCurrentRecovery(recovery)) {
        failed = true;
        const failure = error instanceof Error ? error : new Error("Extension recovery failed.");
        this.errors.set(recovery.id, failure);
        this.onChanged();
        this.logRecovery(recovery, "failed", { error });
      } else {
        this.logRecovery(recovery, "cancelled", { error });
      }
    } finally {
      recovery.status = "idle";
      if (failed && retryable) this.scheduleRecovery(recovery);
    }
  }

  /** Existing rotating app logs retain fixed diagnostics, never native messages, source paths or page/extension payloads. */
  private logRecovery(
    recovery: CrashRecovery,
    event: RecoveryEvent,
    detail: { delayMs?: number; error?: unknown } = {},
  ): void {
    const classes = [
      "Error",
      "TypeError",
      "RangeError",
      "ReferenceError",
      "SyntaxError",
      "ExtensionHostCrashError",
    ];
    const name = detail.error instanceof Error ? detail.error.name : "Error";
    const errorClass = classes.includes(name) ? name : "Error";
    log.info("[browser-extensions] crash recovery", {
      event,
      extensionId: recovery.id,
      ...recovery.crash.diagnostic,
      attempt: recovery.attempts,
      elapsedMs: Math.max(0, Date.now() - recovery.startedAt),
      ...(detail.delayMs === undefined ? {} : { delayMs: detail.delayMs }),
      ...(detail.error === undefined ? {} : { errorClass }),
    });
  }

  /** Invalidating the state also cancels queued/in-flight completion, without altering the durable enabled preference. */
  private cancelRecovery(id: string): void {
    const recovery = this.recoveries.get(id);
    if (!recovery) return;
    if (recovery.timer !== undefined) clearTimeout(recovery.timer);
    this.recoveries.delete(id);
    this.logRecovery(recovery, "cancelled");
  }

  /** Stop timers before app quit. A native load finishing later is unloaded by its invalidated operation. */
  public dispose(): void {
    this.disposed = true;
    for (const id of this.recoveries.keys()) this.cancelRecovery(id);
  }

  /** Pins belong to this local browser profile; disabling retains the preference, removal forgets it. */
  public setPinned(id: string, pinned: boolean): Promise<void> {
    return this.serialize(async () => {
      this.requireEntry(id);
      await this.save(
        this.entries.map((entry) => (entry.id === id ? { ...entry, pinned } : entry)),
      );
    });
  }

  /** Remove managed Store files; unpacked source folders always remain owned by the user. */
  public remove(id: string): Promise<void> {
    this.cancelRecovery(id);
    return this.serialize(async () => {
      const entry = this.requireEntry(id);
      if (entry.source === "store") {
        await this.runtime.uninstall(id);
      } else {
        this.runtime.unload(id);
      }
      await this.save(this.entries.filter((item) => item.id !== id));
      this.errors.delete(id);
    });
  }

  private requireEntry(id: string): BrowserExtensionEntry {
    const entry = this.entries.find((item) => item.id === id);
    if (!entry) {
      throw new Error("Extension is no longer installed. Refresh the extension list.");
    }
    return entry;
  }

  /** Keep installation events and settings mutations ordered, including after a failed operation. */
  private serialize(operation: () => Promise<void>): Promise<void> {
    const pending = this.queue.then(operation);
    this.queue = pending.catch(() => undefined);
    return pending;
  }

  private async save(entries: BrowserExtensionEntry[]): Promise<void> {
    await writeAtomicJson(this.filePath, entries);
    this.entries = entries;
    this.onChanged();
  }
}
