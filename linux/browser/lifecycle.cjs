'use strict';

/** Own hidden-page lifecycle and conservative interruption protection for one view. */
class PageLifecycle {
  constructor(contents, emit, policy, idleMinutes) {
    this.contents = contents;
    this.emit = emit;
    this.policy = policy;
    this.idleMinutes = idleMinutes || 5;
    this.hidden = false;
    this.frozen = false;
    this.audio = false;
    this.capturePermission = false;
    this.downloads = new Set();
    this.uploads = new Set();
    this.lastHidden = 0;
    this.loading = !!contents.isLoading?.();
    contents.on('did-start-loading', () => {this.loading = true; this.update();});
    contents.on('did-stop-loading', () => {this.loading = false; this.update();});
    contents.on('audio-state-changed', event => {
      this.audio = event.audible;
      this.update();
    });
    contents.on('did-navigate', () => {
      this.capturePermission = false;
      this.update();
    });
  }

  /** Capture grants protect until navigation, since Electron has no capture-ended event. */
  get busy() {
    return this.audio || this.capturePermission || this.downloads.size > 0 || this.uploads.size > 0;
  }

  async visibility(hidden) {
    this.hidden = hidden;
    this.lastHidden = hidden ? Date.now() : 0;
    await this.update();
  }

  /** Freeze only idle hidden pages; never send lifecycle commands to protected work. */
  update() {
    // Serialize CDP transitions: a hide/freeze reply must not arrive after a later show.
    this.pending = (this.pending || Promise.resolve()).then(() => this.applyState());
    return this.pending;
  }

  async applyState() {
    if (this.contents.isDestroyed()) return;
    const freeze = this.hidden && this.policy !== 2 && !this.busy && !this.loading && !this.preparing;
    if (freeze !== this.frozen) {
      try {
        if (!this.contents.debugger.isAttached()) this.contents.debugger.attach('1.3');
        await this.contents.debugger.sendCommand('Page.setWebLifecycleState', {
          state: freeze ? 'frozen' : 'active',
        });
        this.frozen = freeze;
      } catch {
        // Failure to freeze is safe: leave the page running and report the limitation.
        this.emit({type: 'warning', message: 'This page could not be put to sleep.'});
      }
    }
    this.emit({type: 'activity', busy: this.busy, audio: this.audio,
      capture: this.capturePermission, downloads: this.downloads.size, uploads: this.uploads.size, frozen: this.frozen});
  }

  canUnload() {
    return this.hidden && this.policy === 0 && !this.busy && !this.loading && !this.preparing && this.lastHidden > 0 &&
      Date.now() - this.lastHidden >= this.idleMinutes * 60000;
  }
}
module.exports = {PageLifecycle};
