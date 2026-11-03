import type { Session } from "electron";
import log from "electron-log";
import { z } from "zod";

const BackgroundSchema = z.object({
  background: z.object({ service_worker: z.string().optional() }).optional(),
});

interface ExtensionEventDeliveryOptions {
  profile: Session;
  emit(id: string, name: string, ...args: unknown[]): void;
  hasWorkerListener(id: string, name: string, ...args: unknown[]): boolean;
  waitForWorkerReady(versionId: number): Promise<boolean>;
  onError?(failure: ExtensionEventDeliveryFailure): void;
}

interface ExtensionEventDeliveryFailure {
  id: string;
  name: string;
  error: unknown;
}

/** Serialize browser events per extension and wake idle MV3 workers before delivering to their listeners. */
export function createExtensionEventDelivery(options: ExtensionEventDeliveryOptions) {
  const deliveries = new Map<string, Promise<void>>();
  const generations = new Map<string, number>();

  /** An old queued event must never cross disable/re-enable or extension replacement. */
  function changed(_event: Electron.Event, extension: Electron.Extension): void {
    generations.set(extension.id, (generations.get(extension.id) ?? 0) + 1);
  }
  options.profile.extensions.on("extension-loaded", changed);
  options.profile.extensions.on("extension-unloaded", changed);

  return function deliverEvent(id: string, name: string, ...args: unknown[]): void {
    const previous = deliveries.get(id) ?? Promise.resolve();
    const generation = generations.get(id) ?? 0;

    async function deliver(): Promise<void> {
      await previous;
      try {
        const extension = options.profile.extensions.getExtension(id);
        if (!extension || generation !== (generations.get(id) ?? 0)) {
          return;
        }
        const { background } = BackgroundSchema.parse(extension.manifest);
        if (background?.service_worker && options.hasWorkerListener(id, name, ...args)) {
          const scope = `chrome-extension://${id}/`;
          const running = Object.entries(options.profile.serviceWorkers.getAllRunning()).find(
            (entry) => entry[1].scope.startsWith(scope),
          );
          let worker = running
            ? options.profile.serviceWorkers.getWorkerFromVersionID(Number(running[0]))
            : undefined;
          if (!worker) {
            worker = await options.profile.serviceWorkers.startWorkerForScope(scope);
          }
          if (!(await options.waitForWorkerReady(worker.versionId))) {
            throw new Error(
              "The extension worker stopped before it could handle the browser event.",
            );
          }
        }
        if (
          options.profile.extensions.getExtension(id) &&
          generation === (generations.get(id) ?? 0)
        ) {
          options.emit(id, name, ...args);
        }
      } catch (error) {
        if (generation !== (generations.get(id) ?? 0)) {
          return;
        }
        log.error(`Extension event ${name} delivery failed for ${id}`, error);
        options.onError?.({ id, name, error });
      }
    }

    // A visible-error callback may fail during shutdown; it must not poison the extension's queue.
    const delivery = deliver().catch((error) => {
      log.error(`Could not report extension event failure for ${id}`, error);
    });
    deliveries.set(id, delivery);
    void delivery.finally(() => {
      if (deliveries.get(id) === delivery) {
        deliveries.delete(id);
      }
    });
  };
}
