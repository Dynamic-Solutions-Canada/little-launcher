import {
  app,
  ipcMain,
  WebContentsView,
  type Extension,
  type IpcMainEvent,
  type IpcMainServiceWorkerEvent,
  type ServiceWorkerMain,
  type Session,
} from "electron";
import log from "electron-log";
import { z } from "zod";
import { ExtensionHostCrashError } from "./runtime-failure.js";

export const STORAGE_OBSERVER_CHANNEL = "paseo:extension-storage-observer";
export const STORAGE_STARTUP_CHANNEL = "paseo:extension-storage-startup";
const STARTUP_TIMEOUT_MS = 5_000;
const ManifestSchema = z.object({
  permissions: z.array(z.string()).optional(),
  background: z.object({ service_worker: z.string().optional() }).optional(),
});
const AreaSchema = z.enum(["local", "session", "sync", "managed"]);
interface ObserverHooks {
  changed(id: string, changes: unknown, area: string): void;
  failed(id: string, error: Error): void;
  ready?(id: string): void;
}
interface ObserverNativeIdentity {
  webContentsId: number;
  osProcessId: number;
  creationTimeMs: number;
}
interface Observer {
  extension: Extension;
  view: WebContentsView;
  url: string;
  scriptUrl: string;
  ready: Promise<void>;
  resolve(): void;
  reject(error: Error): void;
  timer: ReturnType<typeof setTimeout>;
  listening: boolean;
  nativeIdentity?: ObserverNativeIdentity;
  frame?: { processId: number; routingId: number };
  retired: boolean;
  replies: Map<IpcMainServiceWorkerEvent, WorkerBinding>;
}

interface WorkerBinding {
  worker: ServiceWorkerMain;
  observer: Observer;
}

/** Only native storage-capable MV3 workers need the Electron event delivery repair. */
function needsObserver(extension: Extension): boolean {
  const manifest = ManifestSchema.safeParse(extension.manifest);
  return (
    manifest.success &&
    Boolean(manifest.data.background?.service_worker) &&
    manifest.data.permissions?.includes("storage") === true
  );
}

/**
 * Own passive extension-origin storage listeners without creating application windows or tabs.
 * COMPAT(electronWorkerStorageEvents): added in v0.12.0, review removal after 2027-01-19.
 * Electron 44.5.1/44.9 lose native worker notifications (#52451). Remove only after an upstream
 * fix passes the same startup/restart test; storage methods and frame events remain native.
 */
export function registerExtensionStorageObservers(profile: Session, hooks: ObserverHooks) {
  const observers = new Map<string, Observer>();
  const failures = new Map<string, Error>();
  const workers = new Map<number, WorkerBinding>();
  let disposed = false;

  /** One reply failure cannot prevent settling other blocked worker startup calls. */
  function settle(observer: Observer, ready: boolean, binding?: WorkerBinding): boolean {
    let completed = true;
    for (const [event, owner] of observer.replies) {
      if (binding && owner !== binding) continue;
      try {
        event.returnValue = { ready };
      } catch {
        completed = false;
        log.error("Could not settle an extension storage startup reply.");
      } finally {
        observer.replies.delete(event);
      }
    }
    return completed;
  }

  /**
   * Publish native process identity for an explicitly reviewed debugger admission.
   * Chromium IPC process IDs are a different namespace. Native creation time is in milliseconds;
   * a capture must still hold and validate the Windows process's full native birth identity.
   * Missing metrics or a failed diagnostic transport never changes extension availability.
   */
  function publishNativeIdentity(observer: Observer, phase: "ready" | "retired"): void {
    try {
      if (phase === "ready") {
        const contents = observer.view.webContents;
        const osProcessId = contents.getOSProcessId();
        const metric = app.getAppMetrics().find((entry) => entry.pid === osProcessId);
        if (!Number.isSafeInteger(osProcessId) || osProcessId <= 0 || !metric) return;
        if (!Number.isFinite(metric.creationTime) || metric.creationTime <= 0) return;
        observer.nativeIdentity = {
          webContentsId: contents.id,
          osProcessId,
          creationTimeMs: metric.creationTime,
        };
      }
      if (observer.nativeIdentity) {
        log.info("Extension storage observer native process", {
          phase,
          ...observer.nativeIdentity,
        });
      }
    } catch {
      // This optional identity record must not interrupt startup or retirement during teardown.
    }
  }

  /** Fence the source first, then unblock workers and close only this owned passive context. */
  function retire(observer: Observer, error: Error): void {
    if (observer.retired) return;
    observer.retired = true;
    publishNativeIdentity(observer, "retired");
    clearTimeout(observer.timer);
    observer.reject(error);
    settle(observer, false);
    if (!observer.view.webContents.isDestroyed()) {
      try {
        observer.view.webContents.close({ waitForBeforeUnload: false });
      } catch {
        log.error("Could not close an extension storage observer.");
      }
    }
  }

  /** A secondary native unload failure must not skip the remaining observer cleanup or error publication. */
  function unload(id: string): void {
    try {
      profile.extensions.removeExtension(id);
    } catch {
      log.error("Could not unload an extension after storage observation stopped.");
    }
  }

  /** Failed observation unloads runtime state, never the user's persisted enabled preference. */
  function fail(observer: Observer, error: Error): void {
    if (observer.retired || observers.get(observer.extension.id) !== observer) return;
    failures.set(observer.extension.id, error);
    retire(observer, error);
    unload(observer.extension.id);
    hooks.failed(observer.extension.id, error);
  }

  /** Establish identity before navigation; readiness comes from the isolated native-listener IPC. */
  function create(extension: Extension): Observer {
    let resolve!: () => void;
    let reject!: (error: Error) => void;
    const ready = new Promise<void>((yes, no) => {
      resolve = yes;
      reject = no;
    });
    // Loading begins at extension-loaded, before a caller necessarily awaits publication.
    void ready.catch(() => undefined);
    const view = new WebContentsView({
      webPreferences: {
        session: profile,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
      },
    });
    const observer: Observer = {
      extension,
      view,
      url: `chrome-extension://${extension.id}/manifest.json`,
      scriptUrl: new URL(
        ManifestSchema.parse(extension.manifest).background!.service_worker!,
        `chrome-extension://${extension.id}/`,
      ).href,
      ready,
      resolve,
      reject,
      listening: false,
      retired: false,
      replies: new Map(),
      timer: setTimeout(
        () => fail(observer, new Error("Extension storage observer did not become ready.")),
        STARTUP_TIMEOUT_MS,
      ),
    };
    observers.set(extension.id, observer);
    const contents = view.webContents;
    contents.setWindowOpenHandler(() => ({ action: "deny" }));
    // Preserve the actual failed boundary. Never publish preload paths, thrown messages or payloads.
    const lost = (kind: string, detail = "") =>
      fail(
        observer,
        new Error(
          `Extension storage observer failed (${kind}${detail}). Retry the extension in Settings.`,
        ),
      );
    contents.once("destroyed", () => lost("destroyed"));
    contents.on("render-process-gone", (_event, details) => {
      const reasons = [
        "clean-exit",
        "abnormal-exit",
        "killed",
        "crashed",
        "oom",
        "launch-failed",
        "integrity-failure",
        "memory-eviction",
      ];
      const reason = reasons.includes(details.reason) ? details.reason : "unknown";
      const code = Number.isSafeInteger(details.exitCode) ? `; exit=${details.exitCode}` : "";
      if (reason === "crashed") {
        fail(
          observer,
          new ExtensionHostCrashError({
            extensionVersion: extension.version,
            appVersion: app.getVersion(),
            exitCode: details.exitCode,
            webContentsId: contents.id,
            nativeIdentity: observer.nativeIdentity,
          }),
        );
      } else {
        lost("render-process-gone", `; reason=${reason}${code}`);
      }
    });
    contents.on("preload-error", (_event, _path, error) => {
      const classes = [
        "Error",
        "TypeError",
        "RangeError",
        "ReferenceError",
        "SyntaxError",
        "EvalError",
        "URIError",
        "AggregateError",
      ];
      const name = classes.includes(error?.name) ? error.name : "Error";
      lost("preload-error", `; class=${name}`);
    });
    contents.on("will-navigate", (event) => {
      event.preventDefault();
      lost("will-navigate");
    });
    contents.on("will-redirect", (event) => {
      event.preventDefault();
      lost("will-redirect");
    });
    contents.on("did-start-navigation", (details) => {
      if (observer.listening && details.isMainFrame) lost("did-start-navigation");
    });
    contents.on("did-navigate", (_event, url) => {
      if (url !== observer.url) lost("did-navigate");
    });
    // Complete navigation may depend on background startup. It must never release the gate.
    void contents.loadURL(observer.url).catch(() => {
      if (!observer.retired)
        fail(observer, new Error("Could not load the extension storage observer."));
    });
    return observer;
  }

  /** Each native load is a new generation, including reloads with identical path/version bytes. */
  function loaded(_event: Electron.Event, extension: Extension): void {
    const previous = observers.get(extension.id);
    if (previous) retire(previous, new Error("The extension was replaced during startup."));
    observers.delete(extension.id);
    failures.delete(extension.id);
    if (!needsObserver(extension) || disposed) return;
    try {
      create(extension);
    } catch {
      const error = new Error("Could not create the extension storage observer.");
      failures.set(extension.id, error);
      unload(extension.id);
      hooks.failed(extension.id, error);
    }
  }

  function unloaded(_event: Electron.Event, extension: Extension): void {
    const observer = observers.get(extension.id);
    if (observer) retire(observer, new Error("The extension was disabled during startup."));
    observers.delete(extension.id);
  }

  /** Reject navigation swaps, another profile, another extension and every retired generation. */
  function source(event: IpcMainEvent): Observer | undefined {
    const frame = event.senderFrame;
    if (!frame || event.sender.session !== profile) return;
    const observer = [...observers.values()].find((row) => row.view.webContents === event.sender);
    if (!observer || observer.retired || !profile.extensions.getExtension(observer.extension.id))
      return;
    const current = event.sender.mainFrame;
    if (
      frame.url !== observer.url ||
      frame.routingId !== current.routingId ||
      frame.processId !== current.processId ||
      (observer.frame !== undefined &&
        (frame.routingId !== observer.frame.routingId ||
          frame.processId !== observer.frame.processId))
    )
      return;
    return observer;
  }

  function message(event: IpcMainEvent, packet: unknown): void {
    const observer = source(event);
    if (!observer || !packet || typeof packet !== "object") return;
    const input = packet as { kind?: unknown; area?: unknown; changes?: unknown };
    if (input.kind === "ready") {
      if (observer.listening) return;
      const frame = event.senderFrame;
      if (!frame) return;
      observer.frame = { processId: frame.processId, routingId: frame.routingId };
      observer.listening = true;
      publishNativeIdentity(observer, "ready");
      clearTimeout(observer.timer);
      observer.resolve();
      hooks.ready?.(observer.extension.id);
    } else if (input.kind === "change" && observer.listening) {
      const area = AreaSchema.safeParse(input.area);
      if (
        !area.success ||
        !input.changes ||
        typeof input.changes !== "object" ||
        Array.isArray(input.changes)
      )
        return;
      hooks.changed(observer.extension.id, input.changes, area.data);
    }
  }

  /** Bind a new execution instance even when Electron reuses its persistent native version object. */
  function trackWorker(worker: ServiceWorkerMain): void {
    const id = new URL(worker.scope).hostname;
    const observer = observers.get(id);
    if (!observer || observer.retired) {
      worker.ipc.removeAllListeners(STORAGE_STARTUP_CHANNEL);
      worker.ipc.on(STORAGE_STARTUP_CHANNEL, (event) => {
        try {
          event.returnValue = { ready: false };
        } catch {
          log.error("Could not reject an unavailable storage observer.");
        }
      });
      return;
    }
    const binding = { worker, observer };
    workers.set(worker.versionId, binding);
    worker.ipc.removeAllListeners(STORAGE_STARTUP_CHANNEL);
    worker.ipc.on(STORAGE_STARTUP_CHANNEL, (event, ...args: unknown[]) => {
      if (
        args.length !== 0 ||
        disposed ||
        observer.retired ||
        event.session !== profile ||
        event.serviceWorker !== worker ||
        workers.get(worker.versionId) !== binding ||
        observers.get(id) !== observer ||
        !profile.extensions.getExtension(id) ||
        worker.scope !== `chrome-extension://${id}/` ||
        worker.scriptURL !== observer.scriptUrl
      ) {
        try {
          event.returnValue = { ready: false };
        } catch {
          log.error("Could not reject a stale extension storage startup reply.");
        }
        return;
      }
      observer.replies.set(event, binding);
      void releaseWhenReady(binding);
    });
  }

  /** Wait only for the passive frame, never the background entry that this reply blocks. */
  async function releaseWhenReady(binding: WorkerBinding): Promise<void> {
    const { worker, observer } = binding;
    try {
      await observer.ready;
      const current =
        observers.get(observer.extension.id) === observer &&
        !observer.retired &&
        workers.get(worker.versionId) === binding;
      if (!settle(observer, current, binding) && current) {
        fail(observer, new Error("Could not release extension startup."));
      }
    } catch {
      settle(observer, false, binding);
    }
  }

  function stopped(details: { versionId: number; runningStatus: string }): void {
    if (details.runningStatus !== "stopping" && details.runningStatus !== "stopped") return;
    const binding = workers.get(details.versionId);
    if (binding) settle(binding.observer, false, binding);
    workers.delete(details.versionId);
  }

  /** Caller cancellation fails the runtime generation rather than exposing an unobserved worker. */
  async function waitUntilReady(extension: Extension, signal?: AbortSignal): Promise<void> {
    if (!needsObserver(extension)) return;
    const observer = observers.get(extension.id);
    if (
      disposed ||
      !observer ||
      observer.retired ||
      observer.extension.path !== extension.path ||
      observer.extension.version !== extension.version
    )
      throw failures.get(extension.id) ?? new Error("Extension storage observer is unavailable.");
    const cancelled = () => fail(observer, new Error("Extension startup was cancelled."));
    if (signal?.aborted) cancelled();
    signal?.addEventListener("abort", cancelled, { once: true });
    try {
      await observer.ready;
    } finally {
      signal?.removeEventListener("abort", cancelled);
    }
    if (observer.retired || observers.get(extension.id) !== observer)
      throw failures.get(extension.id) ?? new Error("The extension closed during startup.");
  }

  /** Dispose before app quit so no blocked preload or ownerless document can survive shutdown. */
  function dispose(): void {
    if (disposed) return;
    disposed = true;
    app.removeListener("before-quit", dispose);
    profile.extensions.removeListener("extension-loaded", loaded);
    profile.extensions.removeListener("extension-unloaded", unloaded);
    profile.serviceWorkers.removeListener("running-status-changed", stopped);
    ipcMain.removeListener(STORAGE_OBSERVER_CHANNEL, message);
    for (const observer of observers.values()) {
      retire(observer, new Error("Extension storage observation stopped."));
      unload(observer.extension.id);
    }
    observers.clear();
    workers.clear();
  }

  profile.extensions.on("extension-loaded", loaded);
  profile.extensions.on("extension-unloaded", unloaded);
  profile.serviceWorkers.on("running-status-changed", stopped);
  ipcMain.on(STORAGE_OBSERVER_CHANNEL, message);
  app.once("before-quit", dispose);
  return {
    trackWorker,
    waitUntilReady,
    dispose,
    isReady(id: string): boolean {
      const extension = profile.extensions.getExtension(id);
      return Boolean(
        extension &&
        (!needsObserver(extension) ||
          (observers.get(id)?.listening && !observers.get(id)?.retired)),
      );
    },
  };
}
