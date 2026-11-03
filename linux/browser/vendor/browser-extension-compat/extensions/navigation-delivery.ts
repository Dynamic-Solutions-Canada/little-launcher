import { waitForInput } from "../browser-automation/input-lifetime.js";

const METADATA_TIMEOUT_MS = 1_000;
interface CommittedNavigation {
  read(signal: AbortSignal, deadline: number): Promise<string>;
  canPublish(): boolean;
  isCurrent(): boolean;
  publish(transitionType?: string): void;
}
interface PendingNavigation extends CommittedNavigation {
  controller: AbortController;
  deadline: number;
  timer: ReturnType<typeof setTimeout>;
  following: Array<() => void>;
}

/** Order native navigation delivery while one document's bounded metadata read settles.
 * New commits flush the preceding event as metadata-less, then cancel its waiter. Native
 * requests remain owned by the shared CDP transport; no stale result can enrich a new event.
 */
export function createNavigationDelivery(reportFailure: () => void) {
  let pending: PendingNavigation | null = null;

  /** Publish a captured commit first, then the already captured native events that followed it. */
  function finish(current: PendingNavigation, transitionType?: string): void {
    if (pending !== current) return;
    pending = null;
    clearTimeout(current.timer);
    current.controller.abort(new Error("Navigation metadata waiter finished."));
    if (!current.canPublish()) return;
    current.publish(current.isCurrent() ? transitionType : undefined);
    for (const publish of current.following) publish();
  }

  /** Keep cancellation distinct from native failure and enforce the clock even between timer turns. */
  async function resolveMetadata(current: PendingNavigation): Promise<void> {
    try {
      const transitionType = await waitForInput(current.controller.signal, () =>
        current.read(current.controller.signal, current.deadline),
      );
      if (pending !== current) return;
      if (Date.now() >= current.deadline) {
        reportFailure();
        finish(current);
      } else finish(current, transitionType);
    } catch {
      if (pending !== current) return;
      reportFailure();
      finish(current);
    }
  }

  return {
    /** One active metadata waiter per guest; replacement never leaves a chain of queued readers. */
    committed(commit: CommittedNavigation): void {
      if (pending) finish(pending);
      const controller = new AbortController();
      const current: PendingNavigation = {
        ...commit,
        controller,
        following: [],
        deadline: Date.now() + METADATA_TIMEOUT_MS,
        timer: setTimeout(() => {
          reportFailure();
          finish(current);
        }, METADATA_TIMEOUT_MS),
      };
      pending = current;
      void resolveMetadata(current);
    },

    /** Completion and child-frame events cannot overtake their captured top-frame commit. */
    dispatch(publish: () => void): void {
      if (pending) pending.following.push(publish);
      else publish();
    },

    /** Registry retirement and native destruction revoke publication and settle the waiter now. */
    invalidate(): void {
      if (!pending) return;
      const current = pending;
      pending = null;
      clearTimeout(current.timer);
      current.following.length = 0;
      current.controller.abort(new Error("The registered browser guest was retired."));
    },
  };
}
