import { waitForInput } from "./input-lifetime.js";
export type CdpCommandSender = (
  command: string,
  params?: Record<string, unknown>,
) => Promise<unknown>;

export class CdpSessionQueue {
  private pending = 0;
  private queue: Promise<void> = Promise.resolve();

  /** Serialize tasks without releasing an in-flight predecessor when a waiter
   * cancels. Once a task starts, its actual settlement owns the queue barrier.
   */
  public async run<T>(task: () => Promise<T>, signal?: AbortSignal): Promise<T> {
    this.pending += 1;
    const previous = this.queue;
    let releaseCurrent = () => {};
    const current = new Promise<void>((resolve) => {
      releaseCurrent = resolve;
    });
    const tail = previous.catch(() => {}).then(() => current);
    this.queue = tail;

    try {
      if (signal) {
        // A cancelled waiter releases its own queue link, while the previous
        // command still owns the barrier until its actual acknowledgment.
        await waitForInput(signal, () => previous.catch(() => {}));
        signal.throwIfAborted();
      } else {
        await previous.catch(() => {});
      }
      return await task();
    } finally {
      this.pending -= 1;
      releaseCurrent();
      // Retain the tail even when this waiter cancels before its predecessor
      // settles. Resetting here would let a later command bypass that owner.
    }
  }

  /** Refuse background reads instead of accumulating queue debt behind a stalled command. */
  public runIfIdle<T>(task: () => Promise<T>, signal?: AbortSignal): Promise<T> {
    if (this.pending !== 0) return Promise.reject(new Error("Guest CDP queue is busy."));
    return this.run(task, signal);
  }
}
