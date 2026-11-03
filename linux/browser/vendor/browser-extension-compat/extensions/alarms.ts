import { readFile } from "node:fs/promises";
import { z } from "zod";
import { writeAtomicJson } from "./atomic-json.js";

const NameSchema = z.string().refine((name) => Buffer.byteLength(name, "utf8") <= 1024);
const AlarmSchema = z.strictObject({
  name: NameSchema,
  scheduledTime: z.number().finite(),
  periodInMinutes: z.number().finite().optional(),
  persistAcrossSessions: z.boolean(),
});
const InfoSchema = z.object({
  name: NameSchema.optional(),
  when: z.number().finite().optional(),
  delayInMinutes: z.number().finite().optional(),
  periodInMinutes: z.number().finite().optional(),
  persistAcrossSessions: z.boolean().optional(),
});
const StoreSchema = z.record(z.string().regex(/^[a-p]{32}$/), z.array(AlarmSchema).max(500));
export type BrowserExtensionAlarm = z.infer<typeof AlarmSchema>;
type AlarmStore = z.infer<typeof StoreSchema>;
interface AlarmDelivery {
  id: string;
  alarm: BrowserExtensionAlarm;
}
interface AlarmOptions {
  storagePath: string;
  getExtension(id: string): { version: string } | null;
  emit(id: string, event: string, alarm: BrowserExtensionAlarm): void;
  onError(error: unknown): void;
  now?: () => number;
  setTimer?: (callback: () => void, delayMs: number) => () => void;
  /** Chrome's production MV3 minimum is 30 seconds; unpacked developer timing may differ. */
  minimumDelayMs?: (id: string) => number;
}

/** Reject ambiguous or unrepresentable timing and return the default relative delay. */
function validateAlarmTiming(info: z.infer<typeof InfoSchema>): number | undefined {
  if (info.periodInMinutes !== undefined && !Number.isFinite(info.periodInMinutes * 60_000)) {
    throw new Error("Alarm period exceeds the supported time range.");
  }
  if (info.when !== undefined && info.delayInMinutes !== undefined) {
    throw new Error("Cannot set both when and delayInMinutes.");
  }
  const delay = info.delayInMinutes ?? info.periodInMinutes;
  if (info.when === undefined && delay === undefined) {
    throw new Error("Must set at least one of when, delayInMinutes, or periodInMinutes.");
  }
  return delay;
}

/** Own scheduled events independently of worker lifetime, with isolated persistent extension state. */
export function createExtensionAlarms(options: AlarmOptions) {
  const now = options.now ?? Date.now;
  const setTimer =
    options.setTimer ??
    ((callback, delay) => {
      const handle = setTimeout(callback, delay);
      return () => clearTimeout(handle);
    });
  // Chromium's unpacked developer minimum is one second, including nonpositive periods.
  // Never let an injected zero minimum create an immediate repeating timer loop.
  const minimumDelay = (id: string) => Math.max(1_000, options.minimumDelayMs?.(id) ?? 30_000);
  const active = new Set<string>();
  // Chromium AlarmManager::PollAlarms throttles polling per extension, not per alarm name.
  const lastFired = new Map<string, number>();
  let entries: AlarmStore = {};
  let versions: Record<string, string> = {};
  let timer: (() => void) | undefined;
  let disposed = false;
  const restored = restore();
  let queue: Promise<unknown> = restored;
  void restored.catch(options.onError);

  /** Reject corrupt state rather than silently replacing a catalog the user may recover. */
  async function restore(): Promise<void> {
    try {
      const saved = z
        .strictObject({
          alarms: StoreSchema,
          versions: z.record(z.string().regex(/^[a-p]{32}$/), z.string()),
        })
        .parse(JSON.parse(await readFile(options.storagePath, "utf8")));
      for (const alarms of Object.values(saved.alarms)) {
        if (new Set(alarms.map((alarm) => alarm.name)).size !== alarms.length) {
          throw new Error("Saved alarm names must be unique within each extension.");
        }
      }
      entries = Object.fromEntries(
        Object.entries(saved.alarms).map(([id, alarms]) => [
          id,
          alarms.filter((alarm) => alarm.persistAcrossSessions),
        ]),
      );
      versions = saved.versions;
    } catch (error) {
      if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) {
        throw error;
      }
    }
  }

  /** Persist only alarms explicitly surviving reloads, without publishing a failed mutation. */
  async function save(next: AlarmStore, nextVersions = versions): Promise<void> {
    const persistent: AlarmStore = {};
    for (const [id, alarms] of Object.entries(next)) {
      const kept = alarms.filter((alarm) => alarm.persistAcrossSessions);
      if (kept.length > 0) {
        persistent[id] = kept;
      }
    }
    await writeAtomicJson(options.storagePath, { alarms: persistent, versions: nextVersions });
    versions = nextVersions;
    entries = next;
  }

  /** Serialize timers and API mutations so replacement or clear cannot race an elapsed alarm. */
  function serialize<T>(operation: () => Promise<T>): Promise<T> {
    const pending = queue.then(async () => {
      await restored;
      if (disposed) {
        throw new Error("Extension alarm scheduler is closed.");
      }
      return operation();
    });
    queue = pending.catch(() => undefined);
    return pending;
  }

  /** Schedule only enabled extensions, capping long waits to Node's supported timer range. */
  function schedule(): void {
    if (timer !== undefined) {
      timer();
      timer = undefined;
    }
    if (disposed) {
      return;
    }
    let nextTime = Infinity;
    for (const [id, alarms] of Object.entries(entries)) {
      if (active.has(id) && options.getExtension(id) !== null) {
        for (const alarm of alarms) {
          nextTime = Math.min(
            nextTime,
            Math.max(alarm.scheduledTime, (lastFired.get(id) ?? -Infinity) + minimumDelay(id)),
          );
        }
      }
    }
    if (!Number.isFinite(nextTime)) {
      return;
    }
    timer = setTimer(runTimer, Math.max(0, Math.min(2_147_483_647, nextTime - now())));
  }

  /** Join elapsed timers to the same mutation queue and keep failures recoverable. */
  async function runTimer(): Promise<void> {
    timer = undefined;
    try {
      await serialize(fireDue);
    } catch (error) {
      retryAfterFailure(error);
    }
  }

  /** Retry transient persistence failures without spinning on an overdue alarm or stopping all delivery. */
  function retryAfterFailure(error: unknown): void {
    if (!disposed && timer === undefined) {
      timer = setTimer(runTimer, 30_000);
    }
    options.onError(error);
  }

  /** Coalesce missed repeats into one event and schedule the next period from this wake. */
  async function fireDue(): Promise<void> {
    const time = now();
    const next = structuredClone(entries);
    const due: AlarmDelivery[] = [];
    for (const [id, alarms] of Object.entries(entries)) {
      if (
        !active.has(id) ||
        options.getExtension(id) === null ||
        (lastFired.get(id) ?? -Infinity) + minimumDelay(id) > time
      ) {
        continue;
      }
      next[id] = [];
      for (const alarm of alarms) {
        if (alarm.scheduledTime > time) {
          next[id].push(alarm);
          continue;
        }
        due.push({ id, alarm });
        if (alarm.periodInMinutes !== undefined) {
          next[id].push({
            ...alarm,
            scheduledTime: time + Math.max(minimumDelay(id), alarm.periodInMinutes * 60_000),
          });
        }
      }
    }
    if (due.length > 0) {
      await save(next);
      for (const { id } of due) {
        lastFired.set(id, time);
      }
    }
    schedule();
    for (const { id, alarm } of due) {
      // Disable can occur while persistence is pending; it must never wake the old extension.
      if (active.has(id) && options.getExtension(id) !== null && !disposed) {
        try {
          options.emit(id, "alarms.onAlarm", structuredClone(alarm));
        } catch (error) {
          options.onError(error);
        }
      }
    }
  }

  /** Normalize both create(name, info) and create(info), including Chrome's info.name overload. */
  function createAlarm(id: string, args: unknown[]): BrowserExtensionAlarm {
    const separateName = typeof args[0] === "string";
    const separateArgument = args.length === 2;
    if (
      (separateArgument && !separateName && args[0] !== undefined) ||
      (!separateArgument && args.length !== 1)
    ) {
      throw new Error("Invalid alarms.create arguments.");
    }
    const info = InfoSchema.parse(args[separateArgument ? 1 : 0]);
    if (separateName && info.name !== undefined) {
      throw new Error("Cannot set alarm name in both separate argument and object form.");
    }
    const delay = validateAlarmTiming(info);
    const time = now();
    const scheduledTime = Math.max(
      time + minimumDelay(id),
      info.when ?? time + (delay ?? 0) * 60_000,
    );
    return AlarmSchema.parse({
      name: separateName ? NameSchema.parse(args[0]) : (info.name ?? ""),
      scheduledTime,
      periodInMinutes: info.periodInMinutes,
      persistAcrossSessions: info.persistAcrossSessions ?? true,
    });
  }

  return {
    /** Extension permissions are checked by the authenticated compatibility request dispatcher. */
    request(id: string, method: string, args: unknown[]): Promise<unknown> {
      return serialize(async () => {
        if (!active.has(id) || options.getExtension(id) === null) {
          throw new Error("The extension is not enabled.");
        }
        const alarms = entries[id] ?? [];
        if (method === "alarms.create") {
          const alarm = createAlarm(id, args);
          const kept = alarms.filter((item) => item.name !== alarm.name);
          if (alarms.length >= 500) {
            throw new Error("An extension cannot have more than 500 active alarms.");
          }
          await save({ ...entries, [id]: [...kept, alarm] });
          schedule();
          return undefined;
        }
        if (method === "alarms.getAll" || method === "alarms.clearAll") {
          if (args.length !== 0) {
            throw new Error("This alarm method takes no arguments.");
          }
          if (method === "alarms.getAll") {
            return structuredClone(alarms);
          }
          await save({ ...entries, [id]: [] });
          schedule();
          return true;
        }
        if (method !== "alarms.get" && method !== "alarms.clear") {
          throw new Error(`Unsupported alarm method: ${method}`);
        }
        if (args.length > 1) {
          throw new Error("This alarm method takes at most one name.");
        }
        const name = NameSchema.parse(args[0] === undefined ? "" : args[0]);
        const alarm = alarms.find((item) => item.name === name);
        if (method === "alarms.get") {
          return alarm ? structuredClone(alarm) : undefined;
        }
        await save({ ...entries, [id]: alarms.filter((item) => item.name !== name) });
        schedule();
        return Boolean(alarm);
      });
    },
    /** Re-enable persisted alarms only after the extension has actually loaded. */
    loaded(id: string): Promise<void> {
      return serialize(async () => {
        const extension = options.getExtension(id);
        if (!extension) {
          throw new Error("The extension is not enabled.");
        }
        const next = { ...entries };
        if (versions[id] !== extension.version) {
          next[id] = [];
          await save(next, { ...versions, [id]: extension.version });
        }
        active.add(id);
        schedule();
      });
    },
    /** Stop delivery immediately on disable or reload; persistent alarms survive until uninstall. */
    unloaded(id: string): Promise<void> {
      // Electron unloads extensions during shutdown after before-quit has disposed this scheduler.
      if (disposed) {
        return Promise.resolve();
      }
      active.delete(id);
      lastFired.delete(id);
      schedule();
      const pending = serialize(async () => {
        await save({
          ...entries,
          [id]: (entries[id] ?? []).filter((a) => a.persistAcrossSessions),
        });
        schedule();
      });
      return pending.catch((error) => {
        // Shutdown may also win while this unload is queued. Keep the last persisted session state.
        if (!disposed) {
          throw error;
        }
      });
    },
    /** Remove all state on uninstall or extension update, including persisted alarms. */
    forgetExtension(id: string): Promise<void> {
      active.delete(id);
      lastFired.delete(id);
      schedule();
      return serialize(async () => {
        const next = { ...entries };
        delete next[id];
        const nextVersions = { ...versions };
        delete nextVersions[id];
        await save(next, nextVersions);
        schedule();
      });
    },
    /** Session shutdown cancels timers without changing persisted alarms. */
    dispose(): void {
      disposed = true;
      if (timer !== undefined) {
        timer();
        timer = undefined;
      }
    },
  };
}
