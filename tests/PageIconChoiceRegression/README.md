# Page icon regression checks

Run `dotnet run --project tests/PageIconChoiceRegression` with the .NET 10 SDK. No packages or network access are required.

This console harness compiles the production automatic page choice and preview storage code. Small UI/stream adapters allow its asynchronous lifecycle and exact file bytes to be checked on Linux. Synthetic PNG fixtures cover a smaller transparent Retry replacing a larger opaque saved icon, Confirm followed by reload, Cancel, failed Retry, changed URL/type, and disposal during a request. The adapters do not prove WinUI decoding or rendering.

Windows acceptance: start with an adopted automatic page icon, Retry to obtain a different image, Confirm, and compare the settings preview, launcher card, tray, regular-window icon, and a reopened chooser. Reopen the app and verify the same saved image. Repeat Retry then Cancel and Retry while changing the launcher address/type; neither must save the pending image. Existing pinned taskbar icons still require re-pinning under the normal Windows behavior.

Native validation passed on the dedicated WINDOWS-DEV VM with the combined clean 1.41.0.4 Developer MSIX. A synthetic localhost fixture started with an opaque red 512px managed icon. Retry displayed a transparent green 32px preview while the saved file hash remained unchanged. Confirm replaced the file with the exact 32px PNG hash, and both the settings preview and a reopened chooser decoded and displayed the green image. A subsequent Retry displayed the old red image, then Cancel preserved the confirmed green file hash.

The coordinating agent supplied the native results and screenshots, retained locally under [`.artifacts/native-icon-retry`](../../.artifacts/native-icon-retry), with the three synthetic fixture files. This validates the managed-file write, native settings preview, chooser reopening, and Cancel. It does not establish the private Outline page's discovery behavior, app-restart persistence, launcher-card/tray/regular-window rendering, high-contrast behavior, accessibility, or a native URL/type-change scenario. URL/type guards and disposal are covered by the cross-platform harness above.
