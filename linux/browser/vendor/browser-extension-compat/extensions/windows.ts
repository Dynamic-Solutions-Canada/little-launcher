import { app, BrowserWindow, session, type Extension, type Session } from "electron";
import { z } from "zod";
import type { BrowserExtensionTab } from "./tab-projection.js";
import { observeExtensionTab, type BrowserExtensionTabChanges } from "./tab-observation.js";
import { describeVisibleExtensionTab, describeVisibleExtensionTabChanges } from "./tab-access.js";
import { openBrowserExtensionDocumentWindow } from "./document-window.js";

const WindowIdSchema = z.number().int();
const GeometrySchema = z.object({
  left: z.number().int().optional(),
  top: z.number().int().optional(),
  width: z.number().int().positive().optional(),
  height: z.number().int().positive().optional(),
});
const StateSchema = z.enum(["normal", "minimized", "maximized", "fullscreen"]);
const CreateSchema = GeometrySchema.extend({
  type: z.literal("popup"),
  url: z.string(),
  focused: z.boolean().optional(),
  incognito: z.literal(false).optional(),
  state: StateSchema.optional(),
}).strict();
const UpdateSchema = GeometrySchema.extend({
  focused: z.boolean().optional(),
  state: StateSchema.optional(),
}).strict();
const QuerySchema = z.object({
  populate: z.boolean().optional(),
  windowTypes: z.array(z.enum(["normal", "popup", "panel", "app", "devtools"])).optional(),
});

interface ExtensionWindowRequest {
  method: string;
  args: unknown[];
  owner: BrowserWindow | null;
  extension: Extension;
}
interface ExtensionWindowsOptions {
  profile: Session;
  getTabs(windowId: number): BrowserExtensionTab[];
  resolveOwner(window: BrowserWindow): BrowserWindow | null;
  emit(id: string, name: string, ...args: unknown[]): void;
}
interface ExtensionPopout {
  window: BrowserWindow;
  extensionId: string;
}

/** Reject state/bounds combinations Chrome does not accept, without silently changing requested geometry. */
function validateWindowGeometry(info: z.infer<typeof UpdateSchema>): void {
  const hasBounds = [info.left, info.top, info.width, info.height].some(
    (value) => value !== undefined,
  );
  if (info.state && info.state !== "normal" && hasBounds) {
    throw new Error("Window bounds cannot be combined with a non-normal window state.");
  }
  if (info.state === "minimized" && info.focused === true) {
    throw new Error("A minimized window cannot be focused.");
  }
}

/** Apply real native state changes to extension-owned windows. */
function updateWindowState(window: BrowserWindow, state: z.infer<typeof StateSchema>): void {
  if (state === "fullscreen") {
    window.setFullScreen(true);
    return;
  }
  window.setFullScreen(false);
  if (state === "minimized") {
    window.minimize();
  } else if (state === "maximized") {
    window.maximize();
  } else {
    window.restore();
    window.unmaximize();
  }
}

/** Own actual extension popouts separately from toolbar action views and app browser hosts. */
export function createExtensionWindows(options: ExtensionWindowsOptions) {
  const popouts = new Map<number, ExtensionPopout>();

  function isBrowserWindow(window: BrowserWindow): boolean {
    return !window.isDestroyed() && window.webContents.session === session.defaultSession;
  }
  function isPopupWindow(window: BrowserWindow): boolean {
    return popouts.has(window.id) && !window.isDestroyed();
  }
  function isKnownWindow(window: BrowserWindow): boolean {
    return isBrowserWindow(window) || isPopupWindow(window);
  }
  function focusedWindow(): BrowserWindow | null {
    const focused = BrowserWindow.getFocusedWindow();
    if (!focused) {
      return null;
    }
    return isPopupWindow(focused) ? focused : options.resolveOwner(focused);
  }

  /** An extension-created popout has one actual native tab; toolbar popups have none. */
  function getPopupTab(id: number): BrowserExtensionTab | null {
    const popout = [...popouts.values()].find((item) => item.window.webContents.id === id);
    if (!popout || popout.window.isDestroyed()) {
      return null;
    }
    const contents = popout.window.webContents;
    return {
      id: contents.id,
      windowId: popout.window.id,
      active: true,
      highlighted: true,
      url: contents.getURL(),
      title: contents.getTitle(),
      incognito: false,
      status: contents.isLoading() ? "loading" : "complete",
      index: 0,
    };
  }
  function getPopupTabs(): BrowserExtensionTab[] {
    return [...popouts.values()].flatMap(({ window }) => {
      const tab = getPopupTab(window.webContents.id);
      return tab ? [tab] : [];
    });
  }

  function describe(window: BrowserWindow, populate = false, extension?: Extension) {
    let state = "normal";
    if (window.isFullScreen()) {
      state = "fullscreen";
    } else if (window.isMaximized()) {
      state = "maximized";
    } else if (window.isMinimized()) {
      state = "minimized";
    }
    const popup = isPopupWindow(window);
    const bounds = window.getBounds();
    return {
      id: window.id,
      focused: focusedWindow()?.id === window.id,
      incognito: false,
      type: popup ? "popup" : "normal",
      state,
      alwaysOnTop: window.isAlwaysOnTop(),
      left: bounds.x,
      top: bounds.y,
      width: bounds.width,
      height: bounds.height,
      ...(populate
        ? {
            tabs: (popup
              ? getPopupTabs().filter((tab) => tab.windowId === window.id)
              : options.getTabs(window.id)
            ).map((tab) => (extension ? describeVisibleExtensionTab(extension, tab) : tab)),
          }
        : {}),
    };
  }

  /** Report public window lifecycle to enabled extensions in this browser profile. */
  function emit(name: string, ...args: unknown[]): void {
    for (const extension of options.profile.extensions.getAllExtensions()) {
      options.emit(extension.id, name, ...args);
    }
  }

  /** Popout tabs follow the same per-extension visibility rules as ordinary browser tabs. */
  function emitTabCreated(tab: BrowserExtensionTab): void {
    for (const extension of options.profile.extensions.getAllExtensions()) {
      options.emit(extension.id, "tabs.onCreated", describeVisibleExtensionTab(extension, tab));
    }
  }
  function emitTabUpdated(changes: BrowserExtensionTabChanges, tab: BrowserExtensionTab): void {
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

  let focusedId = -1;
  /** Toolbar focus belongs to its host; a real extension popout retains its own window ID. */
  function updateFocus(): void {
    const focused = focusedWindow();
    const id = focused && isKnownWindow(focused) ? focused.id : -1;
    if (id !== focusedId) {
      focusedId = id;
      emit("windows.onFocusChanged", id);
    }
  }
  function observe(window: BrowserWindow): void {
    window.on("focus", updateFocus);
    window.on("blur", updateFocus);
    if (isBrowserWindow(window)) {
      const id = window.id;
      window.once("closed", () => emit("windows.onRemoved", id));
    }
  }

  BrowserWindow.getAllWindows().forEach(observe);
  app.on("browser-window-created", (_event, window) => {
    observe(window);
    if (isBrowserWindow(window)) {
      emit("windows.onCreated", describe(window));
    }
  });
  options.profile.extensions.on("extension-unloaded", (_event, extension) => {
    for (const popout of popouts.values()) {
      if (popout.extensionId === extension.id && !popout.window.isDestroyed()) {
        popout.window.destroy();
      }
    }
  });

  /** Register before navigation so document-start APIs can resolve their actual window and tab. */
  function registerPopout(window: BrowserWindow, extensionId: string): void {
    const windowId = window.id;
    const tabId = window.webContents.id;
    popouts.set(windowId, { window, extensionId });
    const observation = observeExtensionTab({
      contents: window.webContents,
      describe: () => getPopupTab(tabId),
      onUpdated: emitTabUpdated,
    });
    window.once("closed", () => {
      observation.detach();
      popouts.delete(windowId);
      emit("tabs.onRemoved", tabId, { windowId, isWindowClosing: true });
      emit("windows.onRemoved", windowId);
      updateFocus();
    });
    emit("windows.onCreated", describe(window));
    const tab = getPopupTab(tabId);
    if (tab) {
      emitTabCreated(tab);
    }
  }

  /** Read known windows while restricting writes to caller-owned popouts; toolbar views stay excluded. */
  function resolveWindow(
    id: number,
    owner: BrowserWindow | null,
    extension: Extension,
    requireOwnership = false,
  ): BrowserWindow {
    const window = id === -2 ? owner : BrowserWindow.fromId(id);
    if (!window || !isKnownWindow(window)) {
      throw new Error("Unknown browser window.");
    }
    const popout = popouts.get(window.id);
    if (requireOwnership && popout && popout.extensionId !== extension.id) {
      throw new Error("The popup window belongs to another extension.");
    }
    return window;
  }

  /** Open one own-extension document using the same sandbox and profile as toolbar popup hosting. */
  async function createWindow(request: ExtensionWindowRequest) {
    const info = CreateSchema.parse(request.args[0]);
    validateWindowGeometry(info);
    if (!request.owner || !isKnownWindow(request.owner)) {
      throw new Error("Extension popout requires an owning browser window.");
    }
    const owner = resolveWindow(request.owner.id, request.owner, request.extension, true);
    const target = new URL(info.url, `chrome-extension://${request.extension.id}/`);
    const url = target.href;
    const window = await openBrowserExtensionDocumentWindow({
      owner,
      profile: options.profile,
      extension: request.extension,
      url,
      width: info.width,
      height: info.height,
      left: info.left,
      top: info.top,
      show: false,
      onCreated: (created) => registerPopout(created, request.extension.id),
    });
    if (
      window.isDestroyed() ||
      owner.isDestroyed() ||
      !options.profile.extensions.getExtension(request.extension.id)
    ) {
      if (!window.isDestroyed()) {
        window.destroy();
      }
      throw new Error("The extension or owning browser window closed before the popout loaded.");
    }
    // Show without activation first so a requested minimized state survives the display step.
    window.showInactive();
    if (info.state) {
      updateWindowState(window, info.state);
    }
    if (info.state !== "minimized" && info.focused !== false) {
      window.focus();
    }
    return describe(window, true, request.extension);
  }

  /** Restrict writes to caller-owned popouts; app hosts accept focus changes without closure or resizing. */
  function updateWindow(request: ExtensionWindowRequest) {
    const window = resolveWindow(
      WindowIdSchema.parse(request.args[0]),
      request.owner,
      request.extension,
      true,
    );
    const info = UpdateSchema.parse(request.args[1]);
    validateWindowGeometry(info);
    if (!isPopupWindow(window)) {
      if (Object.keys(info).some((key) => key !== "focused")) {
        throw new Error("Only focus can be changed on an app browser window.");
      }
    } else {
      if (info.state) {
        updateWindowState(window, info.state);
      }
      if ([info.left, info.top, info.width, info.height].some((value) => value !== undefined)) {
        const bounds = window.getBounds();
        window.setBounds({
          x: info.left ?? bounds.x,
          y: info.top ?? bounds.y,
          width: info.width ?? bounds.width,
          height: info.height ?? bounds.height,
        });
      }
    }
    if (info.focused === true) {
      window.focus();
    } else if (info.focused === false) {
      window.blur();
    }
    return describe(window, false, request.extension);
  }

  return {
    isPopupWindow,
    getPopupTab,
    getPopupTabs,
    /** Resolve real current-window identity and preserve native callback/promise return values. */
    async request(request: ExtensionWindowRequest): Promise<unknown> {
      const { method, args, owner, extension } = request;
      if (method === "windows.create") {
        return createWindow(request);
      }
      if (method === "windows.update") {
        return updateWindow(request);
      }
      if (method === "windows.remove") {
        const window = resolveWindow(WindowIdSchema.parse(args[0]), owner, extension, true);
        if (!isPopupWindow(window)) {
          throw new Error("An extension cannot close an app browser window.");
        }
        window.close();
        return undefined;
      }
      if (method === "windows.getAll") {
        const query = QuerySchema.parse(args[0] ?? {});
        const types = query.windowTypes ?? ["normal", "popup"];
        return BrowserWindow.getAllWindows()
          .filter(isKnownWindow)
          .filter((window) => types.includes(isPopupWindow(window) ? "popup" : "normal"))
          .map((window) => describe(window, query.populate, extension));
      }
      const id = method === "windows.get" ? WindowIdSchema.parse(args[0]) : -2;
      if (method !== "windows.get" && method !== "windows.getCurrent") {
        throw new Error(`Extension window API ${method} is not supported.`);
      }
      const query = QuerySchema.parse(args[method === "windows.get" ? 1 : 0] ?? {});
      const window = resolveWindow(id, owner, extension);
      if (
        query.windowTypes &&
        !query.windowTypes.includes(isPopupWindow(window) ? "popup" : "normal")
      ) {
        throw new Error("The browser window does not match the requested type.");
      }
      return describe(window, query.populate, extension);
    },
  };
}
