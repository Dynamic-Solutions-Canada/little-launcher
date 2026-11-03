import { contextBridge, ipcRenderer } from "electron";

const CHANNEL = "paseo:extension-compatibility";
interface CompatibilityEvent {
  addListener(callback: (...args: unknown[]) => void, filter?: unknown): void;
  removeListener(callback: (...args: unknown[]) => void): void;
  hasListener(callback: (...args: unknown[]) => void): boolean;
  hasListeners(): boolean;
}
interface CompatibilityChrome {
  runtime: {
    id?: string;
    lastError?: { message: string };
    getManifest(): { permissions?: string[]; action?: unknown; browser_action?: unknown };
  };
  tabs: Record<string, unknown>;
  scripting: {
    executeScript(input: {
      target: { tabId: number; frameIds?: number[]; allFrames?: boolean };
      func: () => string;
    }): Promise<Array<{ frameId: number; documentId: string; result: string }>>;
  };
  webNavigation?: Record<string, unknown>;
  notifications?: Record<string, unknown>;
  permissions?: Record<string, unknown>;
  windows?: Record<string, unknown>;
  contextMenus?: Record<string, unknown>;
  alarms?: Record<string, unknown>;
  action?: Record<string, unknown>;
  browserAction?: Record<string, unknown>;
  storage?: { onChanged: CompatibilityEvent; [area: string]: unknown };
}
declare global {
  var chrome: CompatibilityChrome;
  var paseoExtensionStorageSource: (changes: unknown, area: string) => void;
  var paseoExtensionCompatibility: {
    invoke(method: string, args: unknown[]): Promise<unknown>;
    listen(name: string, callback: (...args: unknown[]) => void): void;
  };
}

/** Isolated preload exposes a narrow authenticated RPC, only to actual extension pages/workers. */
const extensionContext = contextBridge.executeInMainWorld({
  func: () => ({
    id: globalThis.chrome?.runtime?.id,
    href: globalThis.location?.href,
    storage: typeof globalThis.chrome?.storage?.onChanged?.addListener === "function",
    storagePermission:
      globalThis.chrome?.runtime?.getManifest().permissions?.includes("storage") === true,
  }),
});
const isExtension = Boolean(extensionContext.id);
if (isExtension) {
  const eventListeners = new Map<string, Set<(...args: unknown[]) => void>>();
  // One native subscription handles all namespaces, so extensions can register many API events without listener warnings.
  ipcRenderer.on(`${CHANNEL}:event`, (_event, eventName: string, args: unknown[]) => {
    eventListeners.get(eventName)?.forEach((callback) => callback(...args));
  });
  contextBridge.exposeInMainWorld("paseoExtensionCompatibility", {
    invoke: (method: string, args: unknown[]) => ipcRenderer.invoke(CHANNEL, { method, args }),
    listen(name: string, callback: (...args: unknown[]) => void) {
      const listeners = eventListeners.get(name) ?? new Set();
      listeners.add(callback);
      eventListeners.set(name, listeners);
    },
  });
  contextBridge.executeInMainWorld({
    func: installCompatibility,
    args: [process.type === "service-worker"],
  });
  if (extensionContext.storage && extensionContext.storagePermission) {
    if (process.type === "service-worker") {
      // A synchronous worker-thread gate delays entry; main waits only for the passive frame listener.
      const ready = ipcRenderer.sendSync("paseo:extension-storage-startup") as { ready?: boolean };
      if (ready?.ready !== true) throw new Error("Extension storage observation is unavailable.");
    } else if (
      extensionContext.href === `chrome-extension://${extensionContext.id}/manifest.json`
    ) {
      contextBridge.exposeInMainWorld(
        "paseoExtensionStorageSource",
        (changes: unknown, area: string) => {
          ipcRenderer.send("paseo:extension-storage-observer", { kind: "change", changes, area });
        },
      );
      contextBridge.executeInMainWorld({
        func: () => {
          chrome.storage!.onChanged.addListener((changes: unknown, area: unknown) => {
            globalThis.paseoExtensionStorageSource(changes, String(area));
          });
        },
      });
      ipcRenderer.send("paseo:extension-storage-observer", { kind: "ready" });
    }
  }
}

/** Install missing browser-owned namespaces while retaining Chromium storage, scripting and messaging. */
function installCompatibility(isWorker: boolean): void {
  const bridge = globalThis.paseoExtensionCompatibility;
  /** Keep Chromium's cached API object so a binding refresh preserves the installed compatibility methods. */
  function installNamespace(
    name:
      | "webNavigation"
      | "notifications"
      | "permissions"
      | "windows"
      | "contextMenus"
      | "alarms"
      | "action"
      | "browserAction",
    implementation: Record<string, unknown>,
  ): void {
    const namespace = chrome[name];
    if (namespace) {
      Object.assign(namespace, implementation);
    } else {
      chrome[name] = implementation;
    }
  }
  function event(name: string): CompatibilityEvent {
    const listeners = new Set<(...args: unknown[]) => void>();
    bridge.listen(name, (...args) => {
      // Snapshot registrations so a listener added during delivery waits for the next event.
      const registered = Array.from(listeners);
      for (const listener of registered) {
        try {
          listener(...args);
        } catch (error) {
          // Chrome reports one listener's failure without suppressing the remaining listeners.
          if (name.startsWith("storage.")) {
            // Extension-thrown errors can contain storage values; never mirror them into host logs.
            console.error("Extension storage event listener failed.");
          } else {
            console.error("Extension event listener failed", error);
          }
        }
      }
    });
    return {
      addListener(callback, filter) {
        if (filter !== undefined) {
          throw new Error("Filtered extension events are not supported in Paseo.");
        }
        if (typeof callback !== "function") {
          throw new TypeError("An extension event listener must be a function.");
        }
        const alreadySubscribed = listeners.size > 0;
        listeners.add(callback);
        if (!alreadySubscribed) {
          void bridge
            .invoke("events.set-subscription", [{ name, subscribed: true }])
            .catch((error) => {
              console.error("Could not register extension event listener", error);
            });
        }
      },
      removeListener(callback) {
        const removed = listeners.delete(callback);
        if (removed && listeners.size === 0) {
          void bridge
            .invoke("events.set-subscription", [{ name, subscribed: false }])
            .catch((error) => {
              console.error("Could not remove extension event listener", error);
            });
        }
      },
      hasListener(callback) {
        return listeners.has(callback);
      },
      hasListeners() {
        return listeners.size > 0;
      },
    };
  }
  // COMPAT(electronWorkerStorageEvents): added in v0.12.0; review removal after 2027-01-19 (#52451).
  // Keep every storage method native. Only the broken worker Event objects use the existing facade.
  if (
    isWorker &&
    chrome.storage?.onChanged &&
    chrome.runtime.getManifest().permissions?.includes("storage")
  ) {
    Object.assign(chrome.storage.onChanged, event("storage.onChanged"));
    for (const area of ["local", "session", "sync", "managed"]) {
      const storageArea = chrome.storage[area] as { onChanged?: CompatibilityEvent } | undefined;
      if (storageArea?.onChanged) {
        Object.assign(storageArea.onChanged, event(`storage.${area}.onChanged`));
      }
    }
  }

  function callbackResult(
    result: Promise<unknown>,
    callback: unknown,
  ): Promise<unknown> | undefined {
    if (typeof callback !== "function") {
      return result;
    }
    const callbackFunction = callback;
    async function deliverCallback(): Promise<void> {
      let value: unknown;
      try {
        value = await result;
      } catch (error: unknown) {
        const message = error instanceof Error ? error.message : String(error);
        const descriptor = Object.getOwnPropertyDescriptor(chrome.runtime, "lastError");
        Object.defineProperty(chrome.runtime, "lastError", {
          configurable: true,
          get: () => ({ message }),
        });
        try {
          callbackFunction();
        } finally {
          if (descriptor) {
            Object.defineProperty(chrome.runtime, "lastError", descriptor);
          } else {
            delete chrome.runtime.lastError;
          }
        }
        return;
      }
      callbackFunction(value);
    }
    void deliverCallback();
    return undefined;
  }
  function method(name: string) {
    return (...input: unknown[]): Promise<unknown> | undefined => {
      const args = [...input];
      const last = args.at(-1);
      const callback = typeof last === "function" ? last : null;
      if (callback) {
        args.pop();
      }
      const result = bridge.invoke(name, args);
      return callbackResult(result, callback);
    };
  }
  /** Parse CSS with Chromium's own worker-safe parser; only numeric RGBA crosses the host boundary. */
  function badgeColor(value: unknown): unknown {
    if (typeof value !== "string") return value;
    if (value.length > 256) throw new Error("Invalid badge color.");
    const context = new OffscreenCanvas(1, 1).getContext("2d");
    if (!context) throw new Error("Badge color parsing is unavailable.");
    context.fillStyle = "#010203";
    context.fillStyle = value;
    const first = context.fillStyle;
    context.fillStyle = "#040506";
    context.fillStyle = value;
    if (first !== context.fillStyle) throw new Error("Invalid badge color.");
    // Preserve RGB even at zero alpha, which a premultiplied pixel readback would discard.
    if (/^#[0-9a-f]{6}$/i.test(first)) {
      return [1, 3, 5]
        .map((offset) => Number.parseInt(first.slice(offset, offset + 2), 16))
        .concat(255);
    }
    const rgba = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)$/.exec(first);
    if (rgba)
      return [
        Number(rgba[1]),
        Number(rgba[2]),
        Number(rgba[3]),
        Math.round(Number(rgba[4] ?? 1) * 255),
      ];
    context.fillRect(0, 0, 1, 1);
    return Array.from(context.getImageData(0, 0, 1, 1).data);
  }
  function badgeColorMethod(name: string) {
    return (details: unknown, callback?: unknown) => {
      async function invoke(): Promise<unknown> {
        if (!details || typeof details !== "object" || Array.isArray(details))
          throw new Error("Invalid badge details.");
        const input = { ...details } as Record<string, unknown>;
        input.color = badgeColor(input.color);
        return bridge.invoke(name, [input]);
      }
      return callbackResult(invoke(), callback);
    };
  }
  // COMPAT(electronActionBadges): added in v0.12.0; review after 2027-01-19 when Electron's native action stubs gain a usable host API.
  // Electron 44.5.1 explicitly no-ops these six methods; leave every other native action method intact.
  const actionManifest = chrome.runtime.getManifest();
  for (const namespace of ["action", "browserAction"] as const) {
    if (namespace === "action" ? !actionManifest.action : !actionManifest.browser_action) continue;
    installNamespace(namespace, {
      setBadgeText: method(`${namespace}.setBadgeText`),
      getBadgeText: method(`${namespace}.getBadgeText`),
      setBadgeBackgroundColor: badgeColorMethod(`${namespace}.setBadgeBackgroundColor`),
      getBadgeBackgroundColor: method(`${namespace}.getBadgeBackgroundColor`),
      setBadgeTextColor: badgeColorMethod(`${namespace}.setBadgeTextColor`),
      getBadgeTextColor: method(`${namespace}.getBadgeTextColor`),
    });
  }

  installNamespace("webNavigation", {
    onCommitted: event("webNavigation.onCommitted"),
    onCompleted: event("webNavigation.onCompleted"),
    onErrorOccurred: event("webNavigation.onErrorOccurred"),
  });
  /** Obtain document IDs from Chromium scripting results, so navigation races fail the caller's document guard. */
  async function frames(input: { tabId: number; frameId?: number }, all: boolean) {
    const methodName = all ? "webNavigation.getAllFrames" : "webNavigation.getFrame";
    const metadata = await bridge.invoke(methodName, [input]);
    if (metadata === null) {
      return null;
    }
    const target = all
      ? { tabId: input.tabId, allFrames: true }
      : { tabId: input.tabId, frameIds: [input.frameId ?? 0] };
    const injections = await chrome.scripting.executeScript({ target, func: () => location.href });
    function enrich(detail: unknown) {
      if (
        typeof detail !== "object" ||
        detail === null ||
        !("frameId" in detail) ||
        !("url" in detail)
      ) {
        throw new Error("Invalid navigation frame metadata.");
      }
      const injection = injections.find((item) => item.frameId === detail.frameId);
      if (
        !injection ||
        typeof injection.documentId !== "string" ||
        injection.result !== detail.url
      ) {
        throw new Error("The browser document changed while reading frame details.");
      }
      return { ...detail, documentId: injection.documentId };
    }
    return all && Array.isArray(metadata) ? metadata.map(enrich) : enrich(metadata);
  }
  if (chrome.webNavigation) {
    chrome.webNavigation.getFrame = (
      input: { tabId: number; frameId?: number },
      callback?: unknown,
    ) => callbackResult(frames(input, false), callback);
    chrome.webNavigation.getAllFrames = (input: { tabId: number }, callback?: unknown) =>
      callbackResult(frames(input, true), callback);
  }
  installNamespace("notifications", {
    onClicked: event("notifications.onClicked"),
    onButtonClicked: event("notifications.onButtonClicked"),
    onClosed: event("notifications.onClosed"),
    create: method("notifications.create"),
    clear: method("notifications.clear"),
  });
  installNamespace("alarms", {
    onAlarm: event("alarms.onAlarm"),
    create: method("alarms.create"),
    get: method("alarms.get"),
    getAll: method("alarms.getAll"),
    clear: method("alarms.clear"),
    clearAll: method("alarms.clearAll"),
  });
  const contextMenuClicked = event("contextMenus.onClicked");
  const onclickHandlers = new Map<string | number, (...args: unknown[]) => void>();
  contextMenuClicked.addListener((info, tab) => {
    if (
      typeof info === "object" &&
      info !== null &&
      "menuItemId" in info &&
      (typeof info.menuItemId === "string" || typeof info.menuItemId === "number")
    ) {
      onclickHandlers.get(info.menuItemId)?.(info, tab);
    }
  });
  function menuProperties(properties: unknown) {
    if (typeof properties !== "object" || properties === null) {
      throw new Error("Context menu properties are required.");
    }
    const input = { ...properties };
    const onclick = "onclick" in input ? input.onclick : undefined;
    if (onclick !== undefined && typeof onclick !== "function") {
      throw new Error("Context menu onclick must be a function.");
    }
    if (typeof onclick === "function" && typeof window === "undefined") {
      throw new Error("Service workers must use contextMenus.onClicked instead of onclick.");
    }
    if ("onclick" in input) {
      delete input.onclick;
    }
    return { input, onclick };
  }
  installNamespace("contextMenus", {
    onClicked: contextMenuClicked,
    create(properties: unknown, callback?: unknown): string | number {
      const { input, onclick } = menuProperties(properties);
      const values = new Uint32Array(1);
      crypto.getRandomValues(values);
      const id = "id" in input ? input.id : values[0];
      if (typeof id !== "string" && typeof id !== "number") {
        throw new Error("A context menu ID must be a string or number.");
      }
      if ("id" in input) {
        delete input.id;
      }
      const result = bridge.invoke("contextMenus.create", [id, input]).then(() => {
        onclickHandlers.delete(id);
        if (typeof onclick === "function") {
          onclickHandlers.set(id, (...args) => onclick(...args));
        }
        return undefined;
      });
      if (typeof callback === "function") {
        callbackResult(result, callback);
      } else {
        void result.catch((error) => console.error("Context menu creation failed", error));
      }
      return id;
    },
    update(id: unknown, properties: unknown, callback?: unknown) {
      const { input, onclick } = menuProperties(properties);
      async function update(): Promise<unknown> {
        const result = await bridge.invoke("contextMenus.update", [id, input]);
        if ((typeof id === "string" || typeof id === "number") && typeof onclick === "function") {
          onclickHandlers.set(id, (...args) => onclick(...args));
        }
        return result;
      }
      return callbackResult(update(), callback);
    },
    remove(id: unknown, callback?: unknown) {
      async function remove(): Promise<unknown> {
        const result = await bridge.invoke("contextMenus.remove", [id]);
        if (typeof id === "string" || typeof id === "number") {
          onclickHandlers.delete(id);
        }
        return result;
      }
      return callbackResult(remove(), callback);
    },
    removeAll(callback?: unknown) {
      async function removeAll(): Promise<unknown> {
        const result = await bridge.invoke("contextMenus.removeAll", []);
        onclickHandlers.clear();
        return result;
      }
      return callbackResult(removeAll(), callback);
    },
  });
  installNamespace("permissions", {
    onAdded: event("permissions.onAdded"),
    onRemoved: event("permissions.onRemoved"),
    contains: method("permissions.contains"),
    request: method("permissions.request"),
  });
  installNamespace("windows", {
    onCreated: event("windows.onCreated"),
    onRemoved: event("windows.onRemoved"),
    onFocusChanged: event("windows.onFocusChanged"),
    WINDOW_ID_CURRENT: -2,
    WINDOW_ID_NONE: -1,
    getCurrent: method("windows.getCurrent"),
    getAll: method("windows.getAll"),
    get: method("windows.get"),
    create: method("windows.create"),
    update: method("windows.update"),
    remove: method("windows.remove"),
  });
  chrome.tabs.onActivated = event("tabs.onActivated");
  chrome.tabs.onCreated = event("tabs.onCreated");
  chrome.tabs.onRemoved = event("tabs.onRemoved");
  chrome.tabs.onUpdated = event("tabs.onUpdated");
  chrome.tabs.getCurrent = method("tabs.getCurrent");
  chrome.tabs.query = method("tabs.query");
  chrome.tabs.get = method("tabs.get");
}
