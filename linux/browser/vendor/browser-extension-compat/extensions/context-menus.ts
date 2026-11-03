import { readFile } from "node:fs/promises";
import { writeAtomicJson } from "./atomic-json.js";
import type { ContextMenuParams, MenuItemConstructorOptions, Session, WebContents } from "electron";
import { z } from "zod";
import log from "electron-log";
import { matchesExtensionUrl } from "./host-patterns.js";
import { describeExtensionFrame } from "./frames.js";
import type { BrowserExtensionTab } from "./tab-projection.js";
import { describeVisibleExtensionTab } from "./tab-access.js";

const IdSchema = z.union([z.string().min(1), z.number().int()]);
const PropertiesSchema = z.strictObject({
  title: z.string().optional(),
  type: z.enum(["normal", "checkbox", "radio", "separator"]).optional(),
  checked: z.boolean().optional(),
  contexts: z
    .array(
      z.enum(["all", "page", "frame", "selection", "link", "editable", "image", "video", "audio"]),
    )
    .min(1)
    .optional(),
  documentUrlPatterns: z.array(z.string()).optional(),
  targetUrlPatterns: z.array(z.string()).optional(),
  parentId: IdSchema.optional(),
  enabled: z.boolean().optional(),
  visible: z.boolean().optional(),
});
const ItemSchema = PropertiesSchema.extend({ id: IdSchema });
const StoreSchema = z.record(z.string().regex(/^[a-p]{32}$/), z.array(ItemSchema));
type ContextMenuItem = z.infer<typeof ItemSchema>;
type ContextMenuProperties = z.infer<typeof PropertiesSchema>;

interface ContextMenuExtension {
  id: string;
  name: string;
  manifest: unknown;
}
interface ContextMenuOptions {
  storagePath: string;
  getExtension(id: string): ContextMenuExtension | null;
  emit(id: string, event: string, ...args: unknown[]): void;
  onError(error: unknown): void;
}
type PageContext = Pick<
  ContextMenuParams,
  "pageURL" | "frameURL" | "linkURL" | "srcURL" | "mediaType" | "isEditable" | "selectionText"
>;
interface MenuContext {
  params: PageContext;
  frameId: number;
  tab: BrowserExtensionTab;
  isCurrent(): boolean;
}
const PermissionSchema = z.object({ permissions: z.array(z.string()).default([]) });

/** Persist extension-owned menu trees across worker and app restarts; disabled extensions stay hidden. */
export function createExtensionContextMenus(options: ContextMenuOptions) {
  let entries: Record<string, ContextMenuItem[]> = {};
  const restored = restore();
  let queue: Promise<unknown> = restored;
  void restored.catch((error) => log.error("Could not restore extension context menus", error));

  async function restore(): Promise<void> {
    try {
      const saved = StoreSchema.parse(JSON.parse(await readFile(options.storagePath, "utf8")));
      for (const items of Object.values(saved)) {
        const ids = new Set(items.map((item) => item.id));
        if (ids.size !== items.length) {
          throw new Error("Saved context menu IDs must be unique within each extension.");
        }
        for (const item of items) {
          validateItem(item, items);
        }
      }
      entries = saved;
    } catch (error) {
      if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) {
        throw error;
      }
    }
  }

  function permission(id: string): ContextMenuExtension {
    const extension = options.getExtension(id);
    if (
      !extension ||
      !PermissionSchema.parse(extension.manifest).permissions.includes("contextMenus")
    ) {
      throw new Error("The enabled extension must have the contextMenus permission.");
    }
    return extension;
  }

  /** Serialize mutations and publish only successfully persisted trees. */
  function mutate(operation: (next: Record<string, ContextMenuItem[]>) => void): Promise<void> {
    async function save(): Promise<void> {
      await restored;
      const next = structuredClone(entries);
      operation(next);
      await writeAtomicJson(options.storagePath, next);
      entries = next;
    }
    const pending = queue.then(save);
    queue = pending.catch(() => undefined);
    return pending;
  }

  function validateItem(item: ContextMenuItem, items: ContextMenuItem[]): void {
    if (item.type !== "separator" && !item.title) {
      throw new Error("Context menu items require a title.");
    }
    if (item.checked !== undefined && item.type !== "checkbox" && item.type !== "radio") {
      throw new Error("Only checkbox and radio context menu items can be checked.");
    }
    for (const pattern of [
      ...(item.documentUrlPatterns ?? []),
      ...(item.targetUrlPatterns ?? []),
    ]) {
      matchesExtensionUrl(pattern, "https://example.com/");
    }
    let parentId = item.parentId;
    const parents = new Set([item.id]);
    while (parentId !== undefined) {
      if (parents.has(parentId)) {
        throw new Error("A context menu item cannot contain itself or an ancestor.");
      }
      parents.add(parentId);
      const parent = items.find((candidate) => candidate.id === parentId);
      if (!parent || parent.type === "separator") {
        throw new Error(
          "Context menu parent must exist in this extension and cannot be a separator.",
        );
      }
      parentId = parent.parentId;
    }
  }

  async function request(extensionId: string, method: string, args: unknown[]): Promise<void> {
    permission(extensionId);
    const id = method === "contextMenus.removeAll" ? null : IdSchema.parse(args[0]);
    let properties: ContextMenuProperties = {};
    if (method === "contextMenus.create" || method === "contextMenus.update") {
      properties = PropertiesSchema.parse(args[1]);
    }
    await mutate((next) => {
      permission(extensionId);
      const items = next[extensionId] ?? [];
      if (method === "contextMenus.removeAll") {
        delete next[extensionId];
        return;
      }
      if (id === null) {
        throw new Error("A context menu item ID is required.");
      }
      const index = items.findIndex((item) => item.id === id);
      if (method === "contextMenus.create") {
        if (index !== -1) {
          throw new Error("Context menu ID already exists for this extension.");
        }
        const item = { ...properties, id };
        validateItem(item, items);
        next[extensionId] = [...items, item];
        normalizeRadioGroup(next[extensionId], item);
        return;
      }
      if (index === -1) {
        throw new Error("Context menu item does not exist in this extension.");
      }
      if (method === "contextMenus.update") {
        const item = { ...items[index], ...properties, id };
        validateItem(item, items);
        items[index] = item;
        normalizeRadioGroup(items, item);
        next[extensionId] = items;
        return;
      }
      if (method === "contextMenus.remove") {
        const removed = new Set([id]);
        for (let previous = -1; previous !== removed.size; ) {
          previous = removed.size;
          for (const item of items) {
            if (item.parentId !== undefined && removed.has(item.parentId)) {
              removed.add(item.id);
            }
          }
        }
        next[extensionId] = items.filter((item) => !removed.has(item.id));
        return;
      }
      throw new Error(`Extension context menu API ${method} is not supported.`);
    });
  }

  function buildMenuItems(context: MenuContext): MenuItemConstructorOptions[] {
    const result: MenuItemConstructorOptions[] = [];
    for (const [id, items] of Object.entries(entries)) {
      const extension = options.getExtension(id);
      if (
        !extension ||
        !PermissionSchema.parse(extension.manifest).permissions.includes("contextMenus")
      ) {
        continue;
      }
      const children = buildChildren(id, items, undefined, context);
      if (children.length === 1) {
        result.push(...children);
      } else if (children.length > 1) {
        result.push({ label: extension.name, submenu: children });
      }
    }
    return result;
  }

  function buildChildren(
    extensionId: string,
    items: ContextMenuItem[],
    parentId: string | number | undefined,
    context: MenuContext,
  ): MenuItemConstructorOptions[] {
    return items
      .filter((item) => item.parentId === parentId && isVisible(item, context.params))
      .map((item) => {
        const children = buildChildren(extensionId, items, item.id, context);
        const label = (item.title ?? "").replace(/%s/g, () => context.params.selectionText);
        const menu: MenuItemConstructorOptions = {
          label,
          type: item.type ?? "normal",
          enabled: item.enabled ?? true,
          checked: item.checked ?? false,
          click: () => {
            if (!context.isCurrent()) {
              return;
            }
            void click(extensionId, item.id, context).catch((error) => {
              log.error("Extension context menu action failed", error);
              options.onError(error);
            });
          },
        };
        if (children.length > 0) {
          // Electron does not infer submenu type when a normal row type is already specified.
          menu.type = "submenu";
          menu.submenu = children;
        }
        return menu;
      });
  }

  async function click(
    extensionId: string,
    itemId: string | number,
    context: MenuContext,
  ): Promise<void> {
    let currentItem = entries[extensionId]?.find((candidate) => candidate.id === itemId);
    if (!currentItem || currentItem.enabled === false || currentItem.visible === false) {
      return;
    }
    let wasChecked = currentItem.checked ?? false;
    let checked = wasChecked;
    if (currentItem.type === "checkbox" || currentItem.type === "radio") {
      await mutate((next) => {
        permission(extensionId);
        const current = next[extensionId]?.find((candidate) => candidate.id === itemId);
        if (!current) {
          throw new Error("Context menu item was removed before it was selected.");
        }
        wasChecked = current.checked ?? false;
        checked = current.type === "radio" || !wasChecked;
        current.checked = checked;
        normalizeRadioGroup(next[extensionId] ?? [], current);
        currentItem = current;
      });
    }
    const extension = permission(extensionId);
    if (!context.isCurrent()) {
      return;
    }
    const { params } = context;
    const info = {
      menuItemId: currentItem.id,
      parentMenuItemId: currentItem.parentId,
      pageUrl: params.pageURL,
      frameUrl: params.frameURL || undefined,
      frameId: context.frameId,
      linkUrl: params.linkURL || undefined,
      srcUrl: params.srcURL || undefined,
      selectionText: params.selectionText || undefined,
      mediaType: ["image", "video", "audio"].includes(params.mediaType)
        ? params.mediaType
        : undefined,
      editable: params.isEditable,
      ...(currentItem.type === "checkbox" || currentItem.type === "radio"
        ? { wasChecked, checked }
        : {}),
    };
    options.emit(
      extensionId,
      "contextMenus.onClicked",
      info,
      describeVisibleExtensionTab(extension, context.tab),
    );
  }

  return {
    request,
    buildMenuItems,
    async ready(): Promise<void> {
      await restored;
      await queue;
    },
    forgetExtension: (id: string) =>
      mutate((next) => {
        delete next[id];
      }),
  };
}

/** Chrome radio groups are adjacent radio siblings, separated by any other row type. */
function normalizeRadioGroup(items: ContextMenuItem[], selected: ContextMenuItem): void {
  if (selected.type !== "radio" || !selected.checked) {
    return;
  }
  const siblings = items.filter((item) => item.parentId === selected.parentId);
  const index = siblings.findIndex((item) => item.id === selected.id);
  for (const direction of [-1, 1]) {
    for (
      let cursor = index + direction;
      cursor >= 0 && cursor < siblings.length;
      cursor += direction
    ) {
      const sibling = siblings[cursor];
      if (!sibling || sibling.type !== "radio") {
        break;
      }
      sibling.checked = false;
    }
  }
}

function isVisible(item: ContextMenuItem, params: PageContext): boolean {
  if (item.visible === false) {
    return false;
  }
  const contexts = item.contexts ?? ["page"];
  const matchesContext = contexts.some((context) => {
    switch (context) {
      case "all":
      case "page":
        return true;
      case "frame":
        return Boolean(params.frameURL && params.frameURL !== params.pageURL);
      case "selection":
        return Boolean(params.selectionText);
      case "link":
        return Boolean(params.linkURL);
      case "editable":
        return params.isEditable;
      default:
        return params.mediaType === context;
    }
  });
  const documentUrl = params.frameURL || params.pageURL;
  const matchesDocument =
    !item.documentUrlPatterns ||
    item.documentUrlPatterns.some((pattern) => matchesExtensionUrl(pattern, documentUrl));
  const matchesTarget = matchesTargetUrl(item, params);
  return matchesContext && matchesDocument && matchesTarget;
}

/** An image nested in a link has two targets; filter against only the contexts the item requested. */
function matchesTargetUrl(item: ContextMenuItem, params: PageContext): boolean {
  if (!item.targetUrlPatterns) {
    return true;
  }
  const contexts = item.contexts ?? ["page"];
  const targets: string[] = [];
  const linkIsEligible = contexts.includes("all") || contexts.includes("link");
  if (linkIsEligible && params.linkURL) {
    targets.push(params.linkURL);
  }
  const mediaType = params.mediaType;
  const isMedia = mediaType === "image" || mediaType === "audio" || mediaType === "video";
  if (isMedia && params.srcURL) {
    const mediaIsEligible = contexts.includes("all") || contexts.includes(mediaType);
    if (mediaIsEligible) {
      targets.push(params.srcURL);
    }
  }
  return item.targetUrlPatterns.some((pattern) =>
    targets.some((target) => matchesExtensionUrl(pattern, target)),
  );
}

interface BrowserContextMenus {
  buildMenuItems(context: MenuContext): MenuItemConstructorOptions[];
}
interface BrowserMenuProvider {
  menus: BrowserContextMenus;
  getTab(contents: WebContents): BrowserExtensionTab | null;
}
const registeredMenus = new WeakMap<Session, BrowserMenuProvider>();

/** Publish one menu provider per browser profile, shared by actual guests and test profiles. */
export function registerBrowserExtensionContextMenus(
  profile: Session,
  menus: BrowserContextMenus,
  getTab: (contents: WebContents) => BrowserExtensionTab | null,
): void {
  registeredMenus.set(profile, { menus, getTab });
}

/** Add extension-owned rows only for the live page/frame that opened the browser's native menu. */
export function buildBrowserExtensionContextMenuItems(
  contents: WebContents,
  params: ContextMenuParams,
): MenuItemConstructorOptions[] {
  const provider = registeredMenus.get(contents.session);
  const frame = params.frame;
  if (!provider || !frame) {
    return [];
  }
  const detail = describeExtensionFrame(frame, contents.mainFrame);
  const tab = provider.getTab(contents);
  if (tab === null) {
    return [];
  }
  return provider.menus.buildMenuItems({
    params,
    frameId: detail.frameId,
    tab,
    isCurrent: () =>
      !contents.isDestroyed() &&
      !frame.isDestroyed() &&
      contents.getURL() === params.pageURL &&
      frame.url === (params.frameURL || params.pageURL),
  });
}
