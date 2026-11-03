import { contextBridge, ipcRenderer } from "electron";

const ACTIVITY_CHANNEL = "paseo:extension-worker-activity";

declare global {
  var paseoExtensionWorkerActivity: { pulse(): void };
}

// This preload is registered only for service workers, never extension pages or browser frames.
const isExtensionWorker = contextBridge.executeInMainWorld({
  func: () =>
    globalThis.location.protocol === "chrome-extension:" &&
    globalThis.location.hostname === globalThis.chrome?.runtime?.id,
});

if (isExtensionWorker) {
  contextBridge.exposeInMainWorld("paseoExtensionWorkerActivity", {
    pulse: () => ipcRenderer.send(ACTIVITY_CHANNEL),
  });
  contextBridge.executeInMainWorld({ func: observeWebSocketActivity });
}

/** Preserve native WebSocket construction and branding while reporting only actual incoming or successfully sent traffic. */
function observeWebSocketActivity(): void {
  const NativeWebSocket = globalThis.WebSocket;
  const prototype = NativeWebSocket.prototype;
  const readyState = Object.getOwnPropertyDescriptor(prototype, "readyState")?.get;
  const constructor = Object.getOwnPropertyDescriptor(prototype, "constructor");
  if (!readyState || !constructor) {
    throw new Error("Native WebSocket descriptors are required for extension worker activity.");
  }

  const pulse = globalThis.paseoExtensionWorkerActivity.pulse;
  const nativeSend = prototype.send;
  const nativeAddEventListener = EventTarget.prototype.addEventListener;
  const WrappedWebSocket = new Proxy(NativeWebSocket, {
    construct(target, args, newTarget) {
      const socket = Reflect.construct(target, args, newTarget);
      Reflect.apply(nativeAddEventListener, socket, [
        "message",
        (event: Event) => {
          if (event.isTrusted) {
            pulse();
          }
        },
      ]);
      return socket;
    },
  });

  // A proxy keeps native constants, prototype identity and subclass construction behavior.
  Object.defineProperty(prototype, "constructor", { ...constructor, value: WrappedWebSocket });
  prototype.send = new Proxy(nativeSend, {
    apply(target, socket, args) {
      const result = Reflect.apply(target, socket, args);
      // Native send validates its receiver and payload first. Closing/closed sends must not extend worker lifetime.
      if (Reflect.apply(readyState, socket, []) === NativeWebSocket.OPEN) {
        pulse();
      }
      return result;
    },
  });
  globalThis.WebSocket = WrappedWebSocket;
}
