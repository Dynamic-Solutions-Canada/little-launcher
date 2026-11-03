import { Notification } from "electron";
import { randomUUID } from "node:crypto";
import { z } from "zod";

const NotificationSchema = z.object({
  type: z.literal("basic"),
  title: z.string(),
  message: z.string(),
  iconUrl: z.string().optional(),
  buttons: z.array(z.object({ title: z.string() })).optional(),
});

/** Scope native notification IDs and click/close delivery to the requesting extension. */
export function createExtensionNotifications(
  emit: (id: string, name: string, ...args: unknown[]) => void,
) {
  const notifications = new Map<string, Notification>();
  function requestNotification(method: string, args: unknown[], extensionId: string): unknown {
    const hasId = typeof args[0] === "string";
    const id =
      method === "notifications.create" && !hasId ? randomUUID() : z.string().parse(args[0]);
    const key = `${extensionId}:${id}`;
    if (method === "notifications.clear") {
      const notification = notifications.get(key);
      notification?.removeAllListeners();
      notification?.close();
      notifications.delete(key);
      if (notification) {
        emit(extensionId, "notifications.onClosed", id, false);
      }
      return Boolean(notification);
    }
    if (method === "notifications.create") {
      const options = NotificationSchema.parse(hasId ? args[1] : args[0]);
      if (options.buttons && options.buttons.length > 0) {
        throw new Error("Extension notification buttons are not supported on this platform.");
      }
      const notification = new Notification({ title: options.title, body: options.message });
      notifications.get(key)?.removeAllListeners();
      notifications.get(key)?.close();
      notifications.set(key, notification);
      notification.on("click", () => emit(extensionId, "notifications.onClicked", id));
      notification.on("close", () => {
        if (notifications.get(key) === notification) {
          notifications.delete(key);
        }
        emit(extensionId, "notifications.onClosed", id, true);
      });
      notification.show();
      return id;
    }
    throw new Error(`Extension notification API ${method} is not supported.`);
  }

  return {
    request: requestNotification,
    /** Disabled or replaced extensions cannot retain native notifications or their old callbacks. */
    forgetExtension(extensionId: string): void {
      for (const [key, notification] of notifications) {
        if (key.startsWith(`${extensionId}:`)) {
          notifications.delete(key);
          notification.removeAllListeners();
          notification.close();
        }
      }
    },
  };
}
