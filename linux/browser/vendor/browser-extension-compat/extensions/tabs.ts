import { BrowserWindow, webContents, type Session, type WebContents } from "electron";
import { getPaseoBrowserWebviewRegistry } from "../browser-webviews/index.js";
import { createBrowserTabProjection, type BrowserExtensionTab } from "./tab-projection.js";
import { observeExtensionTab, type BrowserExtensionTabChanges } from "./tab-observation.js";
import { describeVisibleExtensionTab, describeVisibleExtensionTabChanges } from "./tab-access.js";

interface ExtensionTabsOptions {
  profile: Session;
  emit(id: string, name: string, ...args: unknown[]): void;
}
interface TabObservation {
  getCurrent(): BrowserExtensionTab;
  detach(): void;
}

/** Publish real identity, selection and guest load changes; retained hidden tabs do not become newly activated. */
export function registerExtensionTabs(options: ExtensionTabsOptions): void {
  const registry = getPaseoBrowserWebviewRegistry();
  const projection = createBrowserTabProjection(options.profile);
  const observed = new Map<number, TabObservation>();
  const watchedOwners = new WeakSet<BrowserWindow>();
  const closingWindowIds = new Set<number>();

  /** Guest destruction precedes owner destruction; retain the real close signal for removal details. */
  function observeOwner(windowId: number): void {
    const owner = BrowserWindow.fromId(windowId);
    if (!owner || watchedOwners.has(owner)) {
      return;
    }
    watchedOwners.add(owner);
    owner.on("close", (event) => {
      closingWindowIds.add(windowId);
      queueMicrotask(() => {
        if (event.defaultPrevented) {
          closingWindowIds.delete(windowId);
        }
      });
    });
    owner.once("closed", () => {
      queueMicrotask(() => closingWindowIds.delete(windowId));
    });
  }

  function emit(name: string, ...args: unknown[]): void {
    for (const extension of options.profile.extensions.getAllExtensions()) {
      options.emit(extension.id, name, ...args);
    }
  }

  /** Tab creation and update payloads use each extension's own metadata grants. */
  function emitCreated(tab: BrowserExtensionTab): void {
    for (const extension of options.profile.extensions.getAllExtensions()) {
      options.emit(extension.id, "tabs.onCreated", describeVisibleExtensionTab(extension, tab));
    }
  }
  function emitUpdated(changes: BrowserExtensionTabChanges, tab: BrowserExtensionTab): void {
    for (const extension of options.profile.extensions.getAllExtensions()) {
      const visibleChanges = describeVisibleExtensionTabChanges(extension, changes, tab);
      if (Object.keys(visibleChanges).length > 0) {
        options.emit(
          extension.id,
          "tabs.onUpdated",
          tab.id,
          visibleChanges,
          describeVisibleExtensionTab(extension, tab),
        );
      }
    }
  }

  function observe(contents: WebContents): void {
    const previous = observed.get(contents.id);
    previous?.detach();
    const initial = projection.describe(contents);
    observeOwner(initial.windowId);
    const observation = observeExtensionTab({
      contents,
      describe: () =>
        registry.getRegistrationForWebContents(contents.id) ? projection.describe(contents) : null,
      onUpdated: emitUpdated,
    });
    function destroyed(): void {
      registry.unregisterWebContents(contents.id);
    }
    contents.once("destroyed", destroyed);
    observed.set(contents.id, {
      getCurrent: observation.getCurrent,
      detach() {
        observation.detach();
        contents.removeListener("destroyed", destroyed);
      },
    });
  }

  // Existing guests get observers without inventing creation events at module registration.
  for (const contents of projection.list()) {
    observe(contents);
  }
  registry.subscribe((event) => {
    if (event.type === "unregistered") {
      const observation = observed.get(event.webContentsId);
      if (observation) {
        observed.delete(event.webContentsId);
        observation.detach();
        emit("tabs.onRemoved", event.webContentsId, {
          windowId: observation.getCurrent().windowId,
          isWindowClosing: closingWindowIds.has(observation.getCurrent().windowId),
        });
      }
      return;
    }
    if (event.webContentsId === null) {
      return;
    }
    const contents = webContents.fromId(event.webContentsId);
    if (!contents || contents.session !== options.profile || contents.isDestroyed()) {
      return;
    }
    if (event.type === "registered") {
      observe(contents);
      emitCreated(projection.describe(contents));
      return;
    }
    const tab = projection.describe(contents);
    emit("tabs.onActivated", { tabId: tab.id, windowId: tab.windowId });
  });
}
