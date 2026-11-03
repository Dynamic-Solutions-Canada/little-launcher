import { BrowserWindow, type Session } from "electron";

interface ExtensionDocumentWindowOptions {
  owner: BrowserWindow;
  profile: Session;
  extension: Electron.Extension;
  url: string;
  width?: number;
  height?: number;
  left?: number;
  top?: number;
  show?: boolean;
  focused?: boolean;
  onCreated?(window: BrowserWindow): void;
}

/** Load an installed extension document in its browser profile, with native geometry in device-independent pixels. */
export async function openBrowserExtensionDocumentWindow({
  owner,
  profile,
  extension,
  url,
  width,
  height,
  left,
  top,
  show = true,
  focused = true,
  onCreated,
}: ExtensionDocumentWindowOptions): Promise<BrowserWindow> {
  const target = new URL(url);
  if (target.protocol !== "chrome-extension:" || target.hostname !== extension.id) {
    throw new Error("Extension windows can only load their own packaged documents.");
  }

  const window = new BrowserWindow({
    parent: owner,
    title: extension.name,
    width,
    height,
    x: left,
    y: top,
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      session: profile,
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));

  /** Apply the same extension-origin boundary to direct navigation and redirects. */
  function guardNavigation(navigation: Electron.Event, targetUrl: string): void {
    const next = new URL(targetUrl);
    if (next.protocol !== "chrome-extension:" || next.hostname !== extension.id) {
      navigation.preventDefault();
    }
  }
  window.webContents.on("will-navigate", (navigation) =>
    guardNavigation(navigation, navigation.url),
  );
  window.webContents.on("will-redirect", guardNavigation);

  function closeWithOwner(): void {
    if (!window.isDestroyed()) {
      // The owner has already closed; an extension's beforeunload cannot retain an orphan window.
      window.destroy();
    }
  }
  owner.on("closed", closeWithOwner);
  window.on("closed", () => owner.removeListener("closed", closeWithOwner));

  try {
    // Identity must be registered before document-start extension API calls.
    onCreated?.(window);
    await window.loadURL(url);
    if (show) {
      if (focused) {
        window.show();
        window.focus();
      } else {
        window.showInactive();
      }
    }
    return window;
  } catch (error) {
    if (!window.isDestroyed()) {
      window.destroy();
    }
    throw error;
  }
}
