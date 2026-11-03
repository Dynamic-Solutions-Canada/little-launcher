import { CdpSessionQueue } from "./cdp-session-queue.js";

/** Minimal native guest surface; creating a transport never observes console or enables domains. */
export interface GuestCdpContents {
  readonly debugger: {
    isAttached(): boolean;
    attach(protocolVersion?: string): void;
    sendCommand(command: string, params?: Record<string, unknown>): Promise<unknown>;
    on?(event: "detach", listener: () => void): void;
    removeListener?(event: "detach", listener: () => void): void;
  };
  isDestroyed(): boolean;
  isDevToolsOpened?(): boolean;
  once(event: "destroyed", listener: () => void): void;
}

const transports = new WeakMap<GuestCdpContents, GuestCdpTransport>();

/** Share one exact native guest's queue and lifetime across automation and navigation readers. */
export function getGuestCdpTransport(contents: GuestCdpContents): GuestCdpTransport {
  const existing = transports.get(contents);
  if (existing) return existing;
  const transport = new GuestCdpTransport(contents);
  transports.set(contents, transport);
  contents.once("destroyed", () => {
    transport.dispose();
    transports.delete(contents);
  });
  return transport;
}

/** Keep native requests owned until settlement, even after their queued caller cancels. Never detach. */
export class GuestCdpTransport {
  private readonly queue = new CdpSessionQueue();
  private readonly lifetime = new AbortController();

  private detachGeneration = 0;
  private readonly nativeDebugger: GuestCdpContents["debugger"];
  private readonly detached = () => {
    this.detachGeneration += 1;
  };

  public constructor(private readonly contents: GuestCdpContents) {
    this.nativeDebugger = contents.debugger;
    this.nativeDebugger.on?.("detach", this.detached);
  }

  /** One metadata observation cannot silently attach again after its native session detaches. */
  public metadataSender(signal: AbortSignal): (command: string) => Promise<unknown> {
    const generation = this.detachGeneration;
    const assertSession = () => {
      signal.throwIfAborted();
      if (generation !== this.detachGeneration) throw new Error("The guest CDP session detached.");
    };
    return async (command) => {
      assertSession();
      const value = await this.sendIfIdle(command, undefined, signal);
      assertSession();
      return value;
    };
  }

  /** Serialize normal commands, retaining the existing automation dispatch callback boundary. */
  public send(
    command: string,
    params?: Record<string, unknown>,
    signal?: AbortSignal,
    onDispatch?: () => void,
  ): Promise<unknown> {
    return this.queue.run(
      () => this.sendImmediate(command, params, onDispatch),
      this.signal(signal),
    );
  }

  /** Best-effort metadata reads never join a busy queue or steal a user's DevTools attachment. */
  public sendIfIdle(
    command: string,
    params: Record<string, unknown> | undefined,
    signal: AbortSignal,
  ): Promise<unknown> {
    return this.queue.runIfIdle(() => {
      if (!this.nativeDebugger.isAttached() && this.contents.isDevToolsOpened?.()) {
        throw new Error("Guest CDP is unavailable while DevTools owns the target.");
      }
      return this.sendImmediate(command, params);
    }, this.signal(signal));
  }

  /** Dialog responses and possibly delivered pointer releases retain their existing queue bypass. */
  public sendImmediate(
    command: string,
    params?: Record<string, unknown>,
    onDispatch?: () => void,
  ): Promise<unknown> {
    this.lifetime.signal.throwIfAborted();
    if (this.contents.isDestroyed()) throw new Error("The browser guest has been destroyed.");
    if (!this.nativeDebugger.isAttached()) this.nativeDebugger.attach("1.3");
    onDispatch?.();
    return this.nativeDebugger.sendCommand(command, params ?? {});
  }

  /** Cancel undispatched queue work; an already dispatched native request still owns its barrier. */
  public dispose(): void {
    this.lifetime.abort(new Error("The browser guest has been destroyed."));
    this.nativeDebugger.removeListener?.("detach", this.detached);
  }

  private signal(signal?: AbortSignal): AbortSignal {
    return signal ? AbortSignal.any([this.lifetime.signal, signal]) : this.lifetime.signal;
  }
}
