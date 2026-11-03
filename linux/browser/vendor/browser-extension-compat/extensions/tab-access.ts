import { z } from "zod";
import { matchesExtensionHostGrant } from "./host-patterns.js";
import type { BrowserExtensionTab } from "./tab-projection.js";
import type { BrowserExtensionTabChanges } from "./tab-observation.js";

const ManifestSchema = z.object({
  permissions: z.array(z.string()).default([]),
  host_permissions: z.array(z.string()).default([]),
});
interface ExtensionTabAccess {
  id: string;
  manifest: unknown;
}
export type VisibleBrowserExtensionTab = Omit<BrowserExtensionTab, "url" | "title"> & {
  url?: string;
  title?: string;
};

/** Sensitive tab fields require tabs permission, an owned extension origin, or a required host grant. */
export function canReadSensitiveTab(
  extension: ExtensionTabAccess,
  tab: BrowserExtensionTab,
): boolean {
  const manifest = ManifestSchema.parse(extension.manifest);
  if (manifest.permissions.includes("tabs")) {
    return true;
  }
  let target: URL;
  try {
    target = new URL(tab.url);
  } catch {
    // Newly created documents may not have committed a URL yet.
    return false;
  }
  if (target.protocol === "chrome-extension:") {
    return target.hostname === extension.id;
  }
  if (!["http:", "https:", "file:", "ftp:"].includes(target.protocol)) {
    return false;
  }
  const grants = [
    ...manifest.host_permissions,
    ...manifest.permissions.filter(
      (permission) => permission === "<all_urls>" || permission.includes("://"),
    ),
  ];
  // Chrome host grants apply to the complete origin, regardless of their path component.
  return grants.some((grant) => matchesExtensionHostGrant(grant, tab.url));
}

/** Preserve public tab identity and state while omitting sensitive fields the caller cannot inspect. */
export function describeVisibleExtensionTab(
  extension: ExtensionTabAccess,
  tab: BrowserExtensionTab,
): VisibleBrowserExtensionTab {
  const visible: VisibleBrowserExtensionTab = { ...tab };
  if (!canReadSensitiveTab(extension, tab)) {
    delete visible.url;
    delete visible.title;
  }
  return visible;
}

/** Filter actual changed properties before event delivery; hidden-only updates do not disclose a change. */
export function describeVisibleExtensionTabChanges(
  extension: ExtensionTabAccess,
  changes: BrowserExtensionTabChanges,
  tab: BrowserExtensionTab,
): BrowserExtensionTabChanges {
  const visible = { ...changes };
  if (!canReadSensitiveTab(extension, tab)) {
    delete visible.url;
    delete visible.title;
  }
  return visible;
}
