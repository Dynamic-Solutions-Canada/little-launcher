interface ExtensionHostCrashInput {
  extensionVersion: string;
  appVersion: string;
  exitCode: number;
  webContentsId: number;
  nativeIdentity?: { osProcessId: number; creationTimeMs: number };
}

/** A confirmed native renderer crash, carrying only bounded host-owned diagnostic metadata. */
export class ExtensionHostCrashError extends Error {
  public readonly diagnostic;

  public constructor(input: ExtensionHostCrashInput) {
    const exitCode = Number.isSafeInteger(input.exitCode) ? input.exitCode : null;
    const suffix = exitCode === null ? "" : `; exit=${exitCode}`;
    super(
      `Extension storage observer failed (render-process-gone; reason=crashed${suffix}). Retry the extension in Settings.`,
    );
    this.name = "ExtensionHostCrashError";
    this.diagnostic = {
      boundary: "render-process-gone",
      reason: "crashed",
      exitCode,
      webContentsId: input.webContentsId,
      osProcessId: input.nativeIdentity?.osProcessId ?? null,
      creationTimeMs: input.nativeIdentity?.creationTimeMs ?? null,
      extensionVersion: ExtensionHostCrashError.version(input.extensionVersion),
      appVersion: ExtensionHostCrashError.version(input.appVersion),
      electronVersion: ExtensionHostCrashError.version(process.versions.electron),
      chromiumVersion: ExtensionHostCrashError.version(process.versions.chrome),
      platform: process.platform,
    };
  }

  /** Manifest/runtime versions are labels, never arbitrary extension text or filesystem paths. */
  private static version(value: string | undefined): string {
    return value && /^[A-Za-z0-9.+-]{1,80}$/.test(value) ? value : "unknown";
  }
}
