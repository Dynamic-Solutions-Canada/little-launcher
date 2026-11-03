import { z } from "zod";

const FrameTreeSchema = z.object({
  frameTree: z.object({
    frame: z.object({ id: z.string(), loaderId: z.string().min(1), url: z.string() }),
  }),
});
const HistorySchema = z.object({
  currentIndex: z.number().int().nonnegative(),
  entries: z.array(z.object({ id: z.number().int(), url: z.string(), transitionType: z.string() })),
});
const coreTypes = new Set([
  "link",
  "typed",
  "auto_bookmark",
  "auto_subframe",
  "manual_subframe",
  "generated",
  "form_submit",
  "reload",
  "keyword",
  "keyword_generated",
]);

interface NavigationMetadataOptions {
  url: string;
  isCurrent(): boolean;
  send(command: string, signal: AbortSignal): Promise<unknown>;
}

/** Read only a committed top frame's native core transition, never infer it from URL equality.
 * A history entry survives reload, so bracket the read with document-loader leases too.
 * CDP omits transition qualifiers and child-frame classification; neither is fabricated here.
 */
export async function readCommittedTransition(
  options: NavigationMetadataOptions,
  signal: AbortSignal,
): Promise<string> {
  function assertCurrent(): void {
    signal.throwIfAborted();
    if (!options.isCurrent()) throw new Error("Navigation metadata belongs to a retired document.");
  }
  async function read(command: string): Promise<unknown> {
    assertCurrent();
    const value = await options.send(command, signal);
    assertCurrent();
    return value;
  }

  const before = FrameTreeSchema.parse(await read("Page.getFrameTree")).frameTree.frame;
  const history = HistorySchema.parse(await read("Page.getNavigationHistory"));
  const after = FrameTreeSchema.parse(await read("Page.getFrameTree")).frameTree.frame;
  const entry = history.entries[history.currentIndex];
  if (
    !entry ||
    before.id !== after.id ||
    before.loaderId !== after.loaderId ||
    before.url !== options.url ||
    after.url !== options.url ||
    entry.url !== options.url
  ) {
    throw new Error("Navigation metadata could not retain its committed document.");
  }
  if (entry.transitionType === "auto_toplevel") return "start_page";
  if (!coreTypes.has(entry.transitionType))
    throw new Error("Unsupported native navigation core type.");
  return entry.transitionType;
}
