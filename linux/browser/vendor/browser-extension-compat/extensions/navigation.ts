import { app, type Session, type WebFrameMain } from "electron";
import log from "electron-log";
import { describeExtensionFrame } from "./frames.js";
import { z } from "zod";
import { getPaseoBrowserWebviewRegistry } from "../browser-webviews/index.js";
import { getGuestCdpTransport } from "../browser-automation/cdp-transport.js";
import { readCommittedTransition } from "./navigation-metadata.js";
import { createNavigationDelivery } from "./navigation-delivery.js";

const NavigationManifestSchema = z.object({ permissions: z.array(z.string()).default([]) });

/** Bridge native frame navigation with exact guest/profile authority and ordered MV3 delivery. */
export function registerExtensionNavigation(
  profile: Session,
  emit: (id: string, name: string, ...args: unknown[]) => void,
): void {
  const registry = getPaseoBrowserWebviewRegistry();
  const observers = new Map<number, ReturnType<typeof createNavigationDelivery>>();
  const generations = new Map<string, number>();
  function extensionChanged(_event: Electron.Event, extension: Electron.Extension): void {
    generations.set(extension.id, (generations.get(extension.id) ?? 0) + 1);
  }
  profile.extensions.on("extension-loaded", extensionChanged);
  profile.extensions.on("extension-unloaded", extensionChanged);
  registry.subscribe((event) => {
    if (event.type === "unregistered") observers.get(event.webContentsId)?.invalidate();
  });

  app.on("web-contents-created", (_createdEvent, contents) => {
    if (contents.session !== profile) return;
    const delivery = createNavigationDelivery(() => {
      // No native error objects, URLs, history, extension payloads or storage values in diagnostics.
      log.warn("[browser-extensions] navigation metadata unavailable");
    });
    const contentsId = contents.id;
    observers.set(contentsId, delivery);
    let commitSequence = 0;
    contents.once("destroyed", () => {
      delivery.invalidate();
      observers.delete(contentsId);
    });

    /** Capture authority and native timestamp now, before a metadata request can yield. */
    function navigation(name: string, url: string, frame: WebFrameMain, error?: string) {
      const topCommit = name === "webNavigation.onCommitted" && frame === contents.mainFrame;
      if (topCommit) commitSequence += 1;
      const registration = registry.getRegistrationForWebContents(contentsId);
      if (!registration) return;
      const recipients = profile.extensions
        .getAllExtensions()
        .filter((extension) =>
          NavigationManifestSchema.parse(extension.manifest).permissions.includes("webNavigation"),
        )
        .map((extension) => ({ id: extension.id, generation: generations.get(extension.id) ?? 0 }));
      if (!recipients.length) {
        if (topCommit) delivery.invalidate();
        return;
      }
      const detail = {
        ...describeExtensionFrame(frame, contents.mainFrame),
        tabId: contentsId,
        url,
        timeStamp: Date.now(),
        error,
      };
      const canPublish = () =>
        !contents.isDestroyed() &&
        registry.getRegistrationForWebContents(contentsId) === registration;
      function publish(transitionType?: string): void {
        if (!canPublish()) return;
        for (const recipient of recipients) {
          if (
            (generations.get(recipient.id) ?? 0) === recipient.generation &&
            profile.extensions.getExtension(recipient.id)
          ) {
            emit(recipient.id, name, transitionType ? { ...detail, transitionType } : detail);
          }
        }
      }
      if (!topCommit) {
        delivery.dispatch(() => publish());
        return;
      }

      const sequence = commitSequence;
      const isCurrent = () =>
        canPublish() &&
        commitSequence === sequence &&
        contents.mainFrame === frame &&
        frame.url === url &&
        recipients.some(
          (recipient) =>
            (generations.get(recipient.id) ?? 0) === recipient.generation &&
            profile.extensions.getExtension(recipient.id),
        );
      const transport = getGuestCdpTransport(contents);
      // COMPAT(electronNavigationTransition): added in v0.12.0; review after 2027-01-20 when Electron exposes committed NavigationHandle transition metadata.
      // Native Electron 44.5.1 omits the core type. Preserve missing metadata on a failed read.
      delivery.committed({
        canPublish,
        isCurrent,
        publish,
        read: (signal, deadline) => {
          const send = transport.metadataSender(signal);
          return readCommittedTransition(
            {
              url,
              isCurrent: () => Date.now() < deadline && isCurrent(),
              send: (command) => send(command),
            },
            signal,
          );
        },
      });
    }
    function findFrame(process: number, routing: number) {
      return contents.mainFrame.framesInSubtree.find(
        (item) => item.processId === process && item.routingId === routing,
      );
    }
    function notify(
      name: string,
      url: string,
      process: number,
      routing: number,
      error?: string,
    ): void {
      const frame = findFrame(process, routing);
      if (frame) navigation(name, url, frame, error);
    }
    contents.on("did-frame-navigate", (_event, url, _code, _text, _main, process, routing) =>
      notify("webNavigation.onCommitted", url, process, routing),
    );
    contents.on("did-frame-finish-load", (_event, _main, process, routing) => {
      const frame = findFrame(process, routing);
      if (frame) notify("webNavigation.onCompleted", frame.url, process, routing);
    });
    contents.on("did-fail-load", (_event, _code, description, url, _main, process, routing) =>
      notify("webNavigation.onErrorOccurred", url, process, routing, description),
    );
  });
}
