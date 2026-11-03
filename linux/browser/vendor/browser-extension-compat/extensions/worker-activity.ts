import path from "node:path";
import type { ServiceWorkerMain, Session } from "electron";

const ACTIVITY_CHANNEL = "paseo:extension-worker-activity";

/** Accept activity only from a running worker belonging to an enabled extension in this profile. */
function isEnabledExtensionWorker(profile: Session, worker: ServiceWorkerMain): boolean {
  if (worker.isDestroyed()) {
    return false;
  }

  const script = new URL(worker.scriptURL);
  const scope = new URL(worker.scope);
  return (
    script.protocol === "chrome-extension:" &&
    scope.protocol === "chrome-extension:" &&
    script.hostname === scope.hostname &&
    Boolean(profile.extensions.getExtension(script.hostname))
  );
}

/** Restore Chrome's idle reset for real extension-worker WebSocket traffic without keeping idle workers alive. */
export function registerBrowserExtensionWorkerActivity(profile: Session): void {
  const registeredWorkers = new WeakSet<ServiceWorkerMain>();
  profile.registerPreloadScript({
    type: "service-worker",
    filePath: path.join(__dirname, "worker-activity-preload.js"),
  });

  profile.serviceWorkers.on("running-status-changed", (details) => {
    if (details.runningStatus !== "starting") {
      return;
    }

    const worker = profile.serviceWorkers.getWorkerFromVersionID(details.versionId);
    if (!worker || registeredWorkers.has(worker) || !isEnabledExtensionWorker(profile, worker)) {
      return;
    }

    registeredWorkers.add(worker);
    worker.ipc.on(ACTIVITY_CHANNEL, (event, ...args: unknown[]) => {
      if (
        args.length !== 0 ||
        event.session !== profile ||
        event.serviceWorker !== worker ||
        !isEnabledExtensionWorker(profile, worker)
      ) {
        return;
      }

      // Ending a completed task resets the native idle deadline. No task survives this pulse.
      const task = worker.startTask();
      task.end();
    });
  });
}
