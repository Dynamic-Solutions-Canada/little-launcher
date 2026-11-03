import type { WebContents } from "electron";
import type { BrowserExtensionTab } from "./tab-projection.js";

export type BrowserExtensionTabChanges = Partial<
  Pick<BrowserExtensionTab, "url" | "title" | "status">
>;
interface TabObservationOptions {
  contents: WebContents;
  describe(): BrowserExtensionTab | null;
  onUpdated(changes: BrowserExtensionTabChanges, tab: BrowserExtensionTab): void;
}

/** Observe native document/load changes once, retaining the last tab snapshot for removal after destruction. */
export function observeExtensionTab(options: TabObservationOptions) {
  const initial = options.describe();
  if (!initial) {
    throw new Error("A browser tab must be registered before observing it.");
  }
  let currentTab = initial;
  // DOM guests may first register during a document load; preserve its eventual completion.
  let waitingForCompletion = initial.status === "loading";

  /** Commit and completion publish status even when consecutive snapshots match. */
  function update(includeStatus: boolean): void {
    if (options.contents.isDestroyed()) {
      return;
    }
    const next = options.describe();
    if (!next) {
      return;
    }
    const changes: BrowserExtensionTabChanges = {};
    if (next.url !== currentTab.url) {
      changes.url = next.url;
    }
    if (next.title !== currentTab.title) {
      changes.title = next.title;
    }
    if (includeStatus) {
      changes.status = next.status;
    }
    currentTab = next;
    if (Object.keys(changes).length > 0) {
      options.onUpdated(changes, next);
    }
  }

  /** Commit clears per-tab action state first. Only then can an extension recompute its badge. */
  function committed(): void {
    waitingForCompletion = true;
    update(true);
  }

  /** Match Chromium: iframe activity must not repeat a completed document's load cycle. */
  function stopped(): void {
    if (!waitingForCompletion) {
      return;
    }
    waitingForCompletion = false;
    update(true);
  }

  function metadataChanged(): void {
    update(false);
  }

  /** Remove every installed listener; safe on either registry removal or native destruction. */
  function detach(): void {
    const contents = options.contents;
    contents.removeListener("did-stop-loading", stopped);
    contents.removeListener("did-navigate", committed);
    contents.removeListener("did-navigate-in-page", metadataChanged);
    contents.removeListener("page-title-updated", metadataChanged);
    contents.removeListener("destroyed", detach);
  }

  const contents = options.contents;
  contents.on("did-stop-loading", stopped);
  contents.on("did-navigate", committed);
  contents.on("did-navigate-in-page", metadataChanged);
  contents.on("page-title-updated", metadataChanged);
  contents.once("destroyed", detach);
  return { getCurrent: () => currentTab, detach };
}
