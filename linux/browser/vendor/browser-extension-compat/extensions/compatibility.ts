import { createExtensionActionBadges } from "./action-badges.js";
import path from "node:path";
import log from "electron-log";
import { createExtensionWindows } from "./windows.js";
import { registerBrowserExtensionWorkerActivity } from "./worker-activity.js";
import { createExtensionAlarms } from "./alarms.js";
import { getBrowserExtensionSource } from "./catalog.js";
import { createBrowserTabProjection } from "./tab-projection.js";
import { registerExtensionTabs } from "./tabs.js";
import { describeVisibleExtensionTab } from "./tab-access.js";
import { registerExtensionStorageObservers } from "./storage-observers.js";
import { createExtensionEventDelivery } from "./events.js";
import {
  BrowserWindow,
  app,
  dialog,
  ipcMain,
  webContents,
  type Session,
  type ServiceWorkerMain,
  type WebContents,
} from "electron";
import { z } from "zod";
import { matchesExtensionUrl, coversExtensionOrigin } from "./host-patterns.js";
import { registerExtensionNavigation } from "./navigation.js";
import { describeExtensionFrame } from "./frames.js";
import {
  createExtensionContextMenus,
  registerBrowserExtensionContextMenus,
} from "./context-menus.js";
import { createExtensionNotifications } from "./notifications.js";
import {
  getPaseoBrowserWebviewRegistry,
  isPreparedPaseoBrowserWebContents,
} from "../browser-webviews/index.js";

const CHANNEL = "paseo:extension-compatibility";
const EVENTS = `${CHANNEL}:event`;
const RequestSchema = z.object({ method: z.string(), args: z.array(z.unknown()) });
const EventSubscriptionSchema = z.strictObject({
  name: z.enum([
    "tabs.onActivated",
    "tabs.onCreated",
    "tabs.onRemoved",
    "tabs.onUpdated",
    "windows.onCreated",
    "windows.onRemoved",
    "windows.onFocusChanged",
    "webNavigation.onCommitted",
    "webNavigation.onCompleted",
    "webNavigation.onErrorOccurred",
    "contextMenus.onClicked",
    "alarms.onAlarm",
    "notifications.onClicked",
    "notifications.onButtonClicked",
    "notifications.onClosed",
    "permissions.onAdded",
    "permissions.onRemoved",
    "storage.onChanged",
    "storage.local.onChanged",
    "storage.session.onChanged",
    "storage.sync.onChanged",
    "storage.managed.onChanged",
  ]),
  subscribed: z.boolean(),
});
const ManifestSchema = z.object({
  permissions: z.array(z.string()).default([]),
  host_permissions: z.array(z.string()).default([]),
});
const PermissionSchema = z.object({
  permissions: z.array(z.string()).default([]),
  origins: z.array(z.string()).default([]),
});
const QuerySchema = z.strictObject({
  active: z.boolean().optional(),
  currentWindow: z.boolean().optional(),
  windowId: z.number().optional(),
  windowType: z.enum(["normal", "popup", "panel", "app", "devtools"]).optional(),
  url: z.union([z.string(), z.array(z.string())]).optional(),
});

/** Add browser-owned extension APIs and normal MV3 worker wake-up behavior. Native supported APIs remain Chromium-owned. */
export function registerBrowserExtensionCompatibility(
  profile: Session,
  options: {
    onStorageFailure?(id: string, error: Error): void;
    onStorageReady?(id: string): void;
    onActionChanged?(): void;
  } = {},
) {
  registerBrowserExtensionWorkerActivity(profile);
  const popupOwners = new Map<number, number>();
  // Lazy worker subscriptions survive normal sleep, but never extension unload/replacement.
  const workerListeners = new Map<string, Set<string>>();
  const workerGenerations = new Map<string, number>();
  profile.extensions.on("extension-unloaded", (_event, extension) => {
    workerListeners.delete(extension.id);
    workerGenerations.set(extension.id, (workerGenerations.get(extension.id) ?? 0) + 1);
  });
  const readyWorkers = new Map<number, Promise<boolean>>();
  const trackedWorkers = new Map<number, ServiceWorkerMain>();
  const workerReadinessResolvers = new Map<number, (ready: boolean) => void>();
  const deliverEvent = createExtensionEventDelivery({
    profile,
    emit,
    hasWorkerListener(id, name, ...args) {
      const names = workerListeners.get(id);
      return (
        (names?.has(name) ?? false) ||
        (name === "storage.onChanged" &&
          (names?.has(`storage.${String(args[1])}.onChanged`) ?? false))
      );
    },
    async waitForWorkerReady(versionId) {
      const ready = readyWorkers.get(versionId);
      if (!ready) {
        throw new Error("Extension worker has not started.");
      }
      return ready;
    },
    onError({ name, error }) {
      if (name === "contextMenus.onClicked") {
        showContextMenuError(error);
      }
    },
  });
  const storageObservers = registerExtensionStorageObservers(profile, {
    changed: (id, changes, area) => deliverEvent(id, "storage.onChanged", changes, area),
    failed(id, error) {
      options.onStorageFailure?.(id, error);
      // Observer messages are fixed host-owned failure labels. Do not serialize Error stacks or native/preload payloads.
      log.error("Extension storage observation failed", error.message);
    },
    ready: options.onStorageReady,
  });
  const notifications = createExtensionNotifications(deliverEvent);
  profile.extensions.on("extension-unloaded", (_event, extension) =>
    notifications.forgetExtension(extension.id),
  );
  const alarms = createExtensionAlarms({
    storagePath: path.join(profile.storagePath, "little-launcher-extension-alarms.json"),
    getExtension: (id) => profile.extensions.getExtension(id),
    emit: deliverEvent,
    onError: (error) => log.error("Extension alarm scheduling failed", error),
    minimumDelayMs(id) {
      const extension = profile.extensions.getExtension(id);
      if (!extension) {
        throw new Error("Extension is no longer enabled.");
      }
      const source = getBrowserExtensionSource(
        path.join(app.getPath("userData"), "browser-extensions"),
        extension.path,
      );
      // Chrome exempts unpacked developer extensions from its production alarm granularity.
      if (source === "unpacked") {
        return 1_000;
      }
      return 30_000;
    },
  });
  profile.extensions.on("extension-loaded", (_event, extension) => {
    void alarms
      .loaded(extension.id)
      .catch((error) => log.error("Could not load extension alarms", error));
  });
  profile.extensions.on("extension-unloaded", (_event, extension) => {
    void alarms
      .unloaded(extension.id)
      .catch((error) => log.error("Could not unload extension alarms", error));
  });
  app.once("before-quit", () => alarms.dispose());
  const registry = getPaseoBrowserWebviewRegistry();
  const contextMenus = createExtensionContextMenus({
    storagePath: path.join(profile.storagePath, "little-launcher-extension-context-menus.json"),
    getExtension: (id) => profile.extensions.getExtension(id),
    emit: deliverEvent,
    onError: showContextMenuError,
  });
  registerBrowserExtensionContextMenus(profile, contextMenus, (contents) =>
    registry.getRegistrationForWebContents(contents.id) ? tab(contents) : null,
  );

  /** A requested native menu action needs a visible failure even when worker wakeup fails. */
  function showContextMenuError(error: unknown): void {
    const message = error instanceof Error ? error.message : String(error);
    dialog.showErrorBox("Extension action failed", message);
  }

  function extensionForUrl(url: string) {
    const parsed = new URL(url);
    if (parsed.protocol !== "chrome-extension:") {
      throw new Error("Extension API calls require an installed extension origin.");
    }
    const extension = profile.extensions.getExtension(parsed.hostname);
    if (!extension) {
      throw new Error("Extension is no longer enabled.");
    }
    return extension;
  }

  /** Deliver only to extension contexts in the browser profile, never app or remote-page preloads. */
  function emit(id: string, name: string, ...args: unknown[]): void {
    for (const contents of name === "storage.onChanged" ? [] : webContents.getAllWebContents()) {
      if (
        contents.session === profile &&
        contents.getURL().startsWith(`chrome-extension://${id}/`)
      ) {
        contents.send(EVENTS, name, args);
      }
    }
    for (const worker of trackedWorkers.values()) {
      if (!worker.isDestroyed() && worker.scope === `chrome-extension://${id}/`) {
        if (name === "storage.onChanged") {
          // Frames already receive native events. Match Chromium's area-before-global worker order.
          worker.send(EVENTS, `storage.${String(args[1])}.onChanged`, [args[0]]);
        }
        worker.send(EVENTS, name, args);
      }
    }
  }

  const { list: tabs, describe: tab } = createBrowserTabProjection(profile);

  const actionBadges = createExtensionActionBadges({
    hasTab: (id) => tabs().some((contents) => contents.id === id && !contents.isDestroyed()),
    changed: () => options.onActionChanged?.(),
  });
  const badgeTabObservers = new Map<number, () => void>();
  /** A committed top-frame navigation resets per-tab action state before tab update delivery. */
  function observeBadgeTab(contents: WebContents): void {
    badgeTabObservers.get(contents.id)?.();
    const reset = () => actionBadges.forgetTab(contents.id);
    contents.on("did-navigate", reset);
    badgeTabObservers.set(contents.id, () => contents.removeListener("did-navigate", reset));
  }
  for (const contents of tabs()) observeBadgeTab(contents);
  registry.subscribe((event) => {
    if (event.type === "unregistered") {
      badgeTabObservers.get(event.webContentsId)?.();
      badgeTabObservers.delete(event.webContentsId);
      actionBadges.forgetTab(event.webContentsId);
    } else if (event.type === "registered") {
      const contents = webContents.fromId(event.webContentsId);
      if (contents && contents.session === profile) {
        observeBadgeTab(contents);
        // A replacement guest keeps browserId but needs a snapshot for its new native tab ID.
        options.onActionChanged?.();
      }
    }
  });
  profile.extensions.on("extension-unloaded", (_event, extension) =>
    actionBadges.forgetExtension(extension.id),
  );
  profile.extensions.on("extension-loaded", (_event, extension) =>
    actionBadges.forgetExtension(extension.id),
  );

  function ownerWindow(senderId: number | null) {
    if (senderId === null) {
      const focused = BrowserWindow.getFocusedWindow();
      if (!focused) {
        return null;
      }
      const hostId = popupOwners.get(focused.webContents.id);
      const host = hostId === undefined ? null : webContents.fromId(hostId);
      return host ? BrowserWindow.fromWebContents(host) : focused;
    }
    const hostId = popupOwners.get(senderId);
    const contents = webContents.fromId(hostId === undefined ? senderId : hostId);
    return contents ? BrowserWindow.fromWebContents(contents) : null;
  }

  const windows = createExtensionWindows({
    profile,
    getTabs: (windowId) =>
      tabs()
        .map(tab)
        .filter((item) => item.windowId === windowId),
    resolveOwner: (window) => ownerWindow(window.webContents.id),
    emit: deliverEvent,
  });

  /** Query actual browser guests and extension popout tabs; toolbar action popups have no tab. */
  function queryTabs(
    args: unknown[],
    senderId: number | null,
    extension: Electron.Extension,
  ): unknown[] {
    const query = QuerySchema.parse(args[0]);
    const normalTabs = tabs().map(tab);
    const popupTabs = windows.getPopupTabs();
    let available = [...normalTabs, ...popupTabs];
    if (query.windowType === "normal") {
      available = normalTabs;
    } else if (query.windowType === "popup") {
      available = popupTabs;
    } else if (query.windowType !== undefined) {
      return [];
    }
    const owner = ownerWindow(senderId);
    let urls: string[] = [];
    if (typeof query.url === "string") {
      urls = [query.url];
    } else if (Array.isArray(query.url)) {
      urls = query.url;
    }
    return available
      .map((item) => describeVisibleExtensionTab(extension, item))
      .filter((item) => {
        const url = item.url;
        return (
          (query.active === undefined || item.active === query.active) &&
          (!query.currentWindow || item.windowId === owner?.id) &&
          (query.windowId === undefined ||
            item.windowId === (query.windowId === -2 ? owner?.id : query.windowId)) &&
          (urls.length === 0 ||
            (url !== undefined && urls.some((pattern) => matchesExtensionUrl(pattern, url))))
        );
      });
  }

  /** Keep tab lookups and queries on the same guest/window projection, rather than Chromium's partial window metadata. */
  async function requestTab(
    method: string,
    args: unknown[],
    senderId: number | null,
    extension: Electron.Extension,
  ): Promise<unknown> {
    if (method === "tabs.getCurrent") {
      if (senderId === null) {
        return undefined;
      }
      const popupTab = windows.getPopupTab(senderId);
      if (popupTab) {
        return describeVisibleExtensionTab(extension, popupTab);
      }
      const contents = webContents.fromId(senderId);
      if (
        contents &&
        !contents.isDestroyed() &&
        contents.session === profile &&
        registry.getRegistrationForWebContents(senderId)
      ) {
        return describeVisibleExtensionTab(extension, tab(contents));
      }
      return undefined;
    }
    if (method === "tabs.get") {
      const id = z.number().int().positive().parse(args[0]);
      const popupTab = windows.getPopupTab(id);
      if (popupTab) {
        return describeVisibleExtensionTab(extension, popupTab);
      }
      const contents = webContents.fromId(id);
      const registered = registry.getRegistrationForWebContents(id);
      const acceptedGuest = contents && (registered || isPreparedPaseoBrowserWebContents(contents));
      if (!contents || contents.session !== profile || !acceptedGuest) {
        throw new Error("Unknown browser tab.");
      }
      // Document-start content scripts can ask before the renderer's dom-ready identity reaches main.
      const registration = await registry.waitForRegistration(contents.id);
      if (!registration || contents.isDestroyed()) {
        throw new Error("The browser tab closed before it became available.");
      }
      return describeVisibleExtensionTab(extension, tab(contents));
    }
    if (method === "tabs.query") {
      return queryTabs(args, senderId, extension);
    }
    throw new Error(`Extension API ${method} is not supported in Paseo.`);
  }

  /** Record only worker registrations; renderer listeners never require a worker wake-up. */
  function setWorkerSubscription(id: string, senderId: number | null, input: unknown): void {
    const subscription = EventSubscriptionSchema.parse(input);
    if (senderId !== null) {
      return;
    }
    const listeners = workerListeners.get(id) ?? new Set<string>();
    if (subscription.subscribed) {
      listeners.add(subscription.name);
    } else {
      listeners.delete(subscription.name);
    }
    workerListeners.set(id, listeners);
  }

  async function request(url: string, senderId: number | null, input: unknown): Promise<unknown> {
    const extension = extensionForUrl(url);
    const manifest = ManifestSchema.parse(extension.manifest);
    const { method, args } = RequestSchema.parse(input);
    if (/^(action|browserAction)\./.test(method)) {
      if (args.length !== 1) throw new Error("Invalid extension action arguments.");
      return actionBadges.request(extension.id, method, args[0]);
    }
    if (method === "permissions.contains") {
      const requested = PermissionSchema.parse(args[0]);
      return (
        requested.permissions.every((permission) => manifest.permissions.includes(permission)) &&
        requested.origins.every((origin) =>
          manifest.host_permissions.some((grant) => coversExtensionOrigin(grant, origin)),
        )
      );
    }
    if (method === "events.set-subscription") {
      setWorkerSubscription(extension.id, senderId, args[0]);
      return undefined;
    }
    if (method === "permissions.request") {
      // Optional permissions cannot be granted until Paseo owns a consent and persistence flow.
      throw new Error("Optional extension permissions are not supported in Paseo.");
    }
    if (method.startsWith("tabs.")) {
      return requestTab(method, args, senderId, extension);
    }
    if (method === "webNavigation.getFrame" || method === "webNavigation.getAllFrames") {
      if (!manifest.permissions.includes("webNavigation")) {
        throw new Error("The webNavigation permission is required.");
      }
      const frameInput = z
        .object({ tabId: z.number(), frameId: z.number().optional() })
        .parse(args[0]);
      const contents = tabs().find((item) => item.id === frameInput.tabId);
      if (!contents) {
        throw new Error("Unknown browser tab.");
      }
      const details = contents.mainFrame.framesInSubtree.map((frame) =>
        describeExtensionFrame(frame, contents.mainFrame),
      );
      if (method === "webNavigation.getAllFrames") {
        return details;
      }
      return details.find((frame) => frame.frameId === frameInput.frameId) ?? null;
    }
    if (method.startsWith("windows.")) {
      return windows.request({ method, args, owner: ownerWindow(senderId), extension });
    }
    if (method.startsWith("notifications.")) {
      if (!manifest.permissions.includes("notifications")) {
        throw new Error("The notifications permission is required.");
      }
      return notifications.request(method, args, extension.id);
    }
    if (method.startsWith("contextMenus.")) {
      return contextMenus.request(extension.id, method, args);
    }
    if (method.startsWith("alarms.")) {
      if (!manifest.permissions.includes("alarms")) {
        throw new Error("The alarms permission is required.");
      }
      return alarms.request(extension.id, method, args);
    }
    throw new Error(`Extension API ${method} is not supported in Paseo.`);
  }

  require("../../../extension_registry.cjs").registerSessionHandler(profile, CHANNEL, (event: Electron.IpcMainInvokeEvent, input: unknown) => {
    if (event.sender.session !== profile || !event.senderFrame) {
      throw new Error("Extension APIs belong to the browser profile.");
    }
    return request(event.senderFrame.url, event.sender.id, input);
  });
  profile.serviceWorkers.on("running-status-changed", (details) => {
    if (details.runningStatus === "running") {
      // Chromium reports running after evaluating the background entry script, including listener registration.
      workerReadinessResolvers.get(details.versionId)?.(true);
      workerReadinessResolvers.delete(details.versionId);
      return;
    }
    if (details.runningStatus === "stopped" || details.runningStatus === "stopping") {
      workerReadinessResolvers.get(details.versionId)?.(false);
      workerReadinessResolvers.delete(details.versionId);
      readyWorkers.delete(details.versionId);
      trackedWorkers.delete(details.versionId);
      return;
    }
    const worker = profile.serviceWorkers.getWorkerFromVersionID(details.versionId);
    if (!worker || !worker.scope.startsWith("chrome-extension://")) {
      return;
    }
    trackedWorkers.set(details.versionId, worker);
    storageObservers.trackWorker(worker);
    workerReadinessResolvers.get(details.versionId)?.(false);
    const ready = new Promise<boolean>((resolve) =>
      workerReadinessResolvers.set(details.versionId, resolve),
    );
    readyWorkers.set(details.versionId, ready);
    worker.ipc.removeHandler(CHANNEL);
    const extensionId = new URL(worker.scriptURL).hostname;
    const generation = workerGenerations.get(extensionId) ?? 0;
    worker.ipc.handle(CHANNEL, (_event, input: unknown) => {
      // Stale IPC must not mutate subscriptions or state belonging to a replacement extension.
      if (
        worker.isDestroyed() ||
        trackedWorkers.get(worker.versionId) !== worker ||
        generation !== (workerGenerations.get(extensionId) ?? 0)
      ) {
        throw new Error("The extension worker is no longer active.");
      }
      return request(worker.scriptURL, null, input);
    });
  });

  registerExtensionTabs({ profile, emit: deliverEvent });
  registerExtensionNavigation(profile, deliverEvent);

  const preload = path.join(__dirname, "compatibility-preload.js");
  profile.registerPreloadScript({ type: "frame", filePath: preload });
  profile.registerPreloadScript({ type: "service-worker", filePath: preload });
  return {
    getActionBadge: actionBadges.snapshot,
    waitUntilReady: storageObservers.waitUntilReady,
    isExtensionReady: storageObservers.isReady,
    disposeStorageObservers: storageObservers.dispose,
    forgetExtensionContextMenus: contextMenus.forgetExtension,
    forgetExtensionAlarms: alarms.forgetExtension,
    /** Associate popup API calls with the app window that owns the selected browser guest. */
    registerPopup(popup: WebContents, guest: WebContents): void {
      const registration = registry.getRegistrationForWebContents(guest.id);
      if (!registration) {
        throw new Error("Extension popup requires a registered browser tab.");
      }
      popupOwners.set(popup.id, registration.hostWebContentsId);
      popup.once("destroyed", () => popupOwners.delete(popup.id));
    },
  };
}
