import type { WebFrameMain } from "electron";

/** Chromium's extension frame IDs are zero for the main frame and frame-tree node IDs for children. */
export function describeExtensionFrame(frame: WebFrameMain, mainFrame: WebFrameMain) {
  let frameId = frame.frameTreeNodeId;
  if (frame === mainFrame) {
    frameId = 0;
  }
  let parentFrameId = -1;
  if (frame.parent) {
    parentFrameId = frame.parent.frameTreeNodeId;
    if (frame.parent === mainFrame) {
      parentFrameId = 0;
    }
  }
  return { frameId, parentFrameId, url: frame.url, processId: frame.processId };
}
