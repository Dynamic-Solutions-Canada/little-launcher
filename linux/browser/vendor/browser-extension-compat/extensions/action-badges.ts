import { z } from "zod";

const byte = z.number().int().min(0).max(255);
const color = z.tuple([byte, byte, byte, byte]);
const input = z.strictObject({ tabId: z.number().int().min(0).optional() });
const textInput = input.extend({ text: z.string().nullable().optional() });
const colorInput = input.extend({ color });
type Color = z.infer<typeof color>;
interface Overrides {
  text?: string;
  backgroundColor?: Color;
  textColor?: Color;
}
export interface ExtensionActionBadge {
  text: string;
  backgroundColor: Color | null;
  textColor: Color | null;
}

/** Own only extension-provided badge properties. Each instance belongs to one browser profile. */
export function createExtensionActionBadges(options: {
  hasTab(id: number): boolean;
  changed(): void;
}) {
  const actions = new Map<string, { defaults: Overrides; tabs: Map<number, Overrides> }>();
  let queued = false;

  /** Coalesce property changes from one extension turn into one toolbar refresh. */
  function changed(): void {
    if (queued) return;
    queued = true;
    queueMicrotask(() => {
      queued = false;
      options.changed();
    });
  }

  function requireTab(tabId: number | undefined): void {
    if (tabId !== undefined && !options.hasTab(tabId)) throw new Error("Unknown browser tab.");
  }

  /** Resolve properties separately: a tab text override must not hide a later global color. */
  function snapshot(id: string, tabId?: number): ExtensionActionBadge {
    const action = actions.get(id);
    const tab = tabId === undefined ? undefined : action?.tabs.get(tabId);
    const backgroundColor = tab?.backgroundColor ?? action?.defaults.backgroundColor;
    const textColor = tab?.textColor ?? action?.defaults.textColor;
    return {
      text: tab?.text ?? action?.defaults.text ?? "",
      backgroundColor: backgroundColor ? [...backgroundColor] : null,
      textColor: textColor ? [...textColor] : null,
    };
  }

  /** Authenticate the extension outside this store; reject every non-profile or unregistered tab. */
  function request(id: string, method: string, value: unknown): unknown {
    const operation = method.replace(/^(action|browserAction)\./, "");
    const isText = operation.endsWith("BadgeText");
    const isBackground = operation.endsWith("BadgeBackgroundColor");
    const isTextColor = operation.endsWith("BadgeTextColor");
    if (!/^(get|set)Badge(Text|BackgroundColor|TextColor)$/.test(operation)) {
      throw new Error("This extension action operation is not supported.");
    }
    const setting = operation.startsWith("set");
    let property: keyof Overrides = "text";
    if (isBackground) property = "backgroundColor";
    else if (isTextColor) property = "textColor";
    if (!setting) {
      const { tabId } = input.parse(value);
      requireTab(tabId);
      const result = snapshot(id, tabId)[property];
      return result ?? [0, 0, 0, 0];
    }
    const schema = isText ? textInput : colorInput;
    const details = schema.parse(value);
    const { tabId } = details;
    requireTab(tabId);
    const action = actions.get(id) ?? { defaults: {}, tabs: new Map<number, Overrides>() };
    const target = tabId === undefined ? action.defaults : (action.tabs.get(tabId) ?? {});
    if ("color" in details) {
      const rgba = details.color;
      if (isTextColor && rgba[3] === 0)
        throw new Error("Badge text color must not be transparent.");
      if (isBackground) target.backgroundColor = [...rgba];
      else target.textColor = [...rgba];
    } else {
      const { text } = details;
      if (text == null) delete target.text;
      else target.text = text;
    }
    if (tabId !== undefined) action.tabs.set(tabId, target);
    actions.set(id, action);
    changed();
    return undefined;
  }

  /** A closed or newly navigated tab loses only its own overrides, never global defaults. */
  function forgetTab(tabId: number): void {
    for (const action of actions.values()) action.tabs.delete(tabId);
    changed();
  }

  /** Disable/unload discards runtime action state; persisted pin preferences live in the catalog. */
  function forgetExtension(id: string): void {
    actions.delete(id);
    changed();
  }
  return { request, snapshot, forgetTab, forgetExtension };
}
