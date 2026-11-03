import { BrowserWindow, webContents, type Session, type WebContents } from "electron";
import {
  getActivePaseoBrowserWebContentsForHostWindow,
  getPaseoBrowserWebviewRegistry,
} from "../browser-webviews/index.js";

export interface BrowserExtensionTab {
  id: number;
  windowId: number;
  active: boolean;
  highlighted: boolean;
  url: string;
  title: string;
  incognito: boolean;
  status: "loading" | "complete";
  index: number;
}

/** Share the authoritative guest/window projection across tab APIs, native menus and browser events. */
export function createBrowserTabProjection(profile: Session) {
  const registry = getPaseoBrowserWebviewRegistry();

  function list(): WebContents[] {
    return webContents
      .getAllWebContents()
      .filter(
        (contents) =>
          contents.session === profile &&
          registry.getRegistrationForWebContents(contents.id) !== null,
      );
  }

  /** Describe only registered browser guests; index belongs to the owning app window. */
  function describe(contents: WebContents): BrowserExtensionTab {
    const registration = registry.getRegistrationForWebContents(contents.id);
    if (!registration) {
      throw new Error("This page is not a Paseo browser tab.");
    }
    const host = webContents.fromId(registration.hostWebContentsId);
    const owner = host ? BrowserWindow.fromWebContents(host) : null;
    const activeGuest = getActivePaseoBrowserWebContentsForHostWindow(
      registration.hostWebContentsId,
    );
    const windowTabs = list().filter(
      (guest) =>
        registry.getRegistrationForWebContents(guest.id)?.hostWebContentsId ===
        registration.hostWebContentsId,
    );
    const active = activeGuest?.id === contents.id;
    return {
      id: contents.id,
      windowId: owner ? owner.id : -1,
      active,
      highlighted: active,
      url: contents.getURL(),
      title: contents.getTitle(),
      incognito: false,
      status: contents.isLoading() ? "loading" : "complete",
      index: windowTabs.findIndex((guest) => guest.id === contents.id),
    };
  }
  return { list, describe };
}
