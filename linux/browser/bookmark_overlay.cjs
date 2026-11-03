'use strict';
const {WebContentsView}=require('electron');
const path=require('node:path');
const {bookmarkVersion,resolveBookmark}=require('./bookmarks.cjs');

/** Local trusted surface above remote content. Page views never receive its preload or drag state. */
class BookmarkOverlay {
  constructor(owner) {
    this.owner=owner;this.drag=null;this.selection=null;
    const nativeWindow=owner.window;
    nativeWindow.once('closed',()=>this.dispose());
  }
  contents() {return [!this.owner.window.isDestroyed() && this.owner.window.webContents,this.view?.webContents].filter(value=>value && !value.isDestroyed());}
  broadcastDrag() {for(const contents of this.contents())contents.send('bookmark-drag-state',this.drag);}
  action(action,value) {
    if(action==='bookmark-drag-start') {
      const source=this.owner.bookmarkPath(value?.source);
      if(this.owner.canEditBookmarks() && source && typeof value.token==='string' && /^[\da-f-]{36}$/.test(value.token) && value.version===bookmarkVersion(this.owner.config.WebBookmarks || [])) {
        clearTimeout(this.dragEndTimer);
        this.drag={source,version:value.version,token:value.token};this.broadcastDrag();
      }
      return true;
    }
    if(action==='bookmark-drag-end') {
      if(value===this.drag?.token) {
        // Native dragend and drop come from different renderers; their IPC can cross.
        clearTimeout(this.dragEndTimer);
        this.dragEndTimer=setTimeout(()=>{if(this.drag?.token===value){this.drag=null;this.broadcastDrag();}},250);
      }
      return true;
    }
    if(action==='bookmark-overlay-close') {this.close();return true;}
    return false;
  }
  validDrop(value) {
    return !!this.drag && value.token===this.drag.token && value.version===this.drag.version && JSON.stringify(value.source)===JSON.stringify(this.drag.source);
  }
  open(folder,anchor,overflow=0) {
    const version=bookmarkVersion(this.owner.config.WebBookmarks || []);
    if(this.selection && JSON.stringify(this.selection.folder)===JSON.stringify(folder) && !this.drag) {this.close();return;}
    this.selection={folder,overflow,anchor:anchor && Number.isFinite(anchor.x) ? {x:anchor.x,y:anchor.y}: {x:8},version};
    if(!this.ticket)this.ticket=this.owner.beginDialog();
    if(!this.view) {
      this.view=new WebContentsView({webPreferences:{preload:path.join(__dirname,'preload.cjs'),sandbox:true,contextIsolation:true,nodeIntegration:false}});
      const contents=this.view.webContents;
      this.view.setBackgroundColor('#00000000');
      contents.on('will-navigate',event=>event.preventDefault());
      contents.setWindowOpenHandler(()=>({action:'deny'}));
      contents.on('did-finish-load',()=>{contents.setZoomMode('disabled');this.update();this.broadcastDrag();if(this.selection && !this.drag)contents.focus();});
      contents.on('before-input-event',(event,input)=>{if(input.type==='keyDown' && input.key==='Escape'){event.preventDefault();this.close();}});
      void contents.loadFile(path.join(__dirname,'bookmark_overlay.html'));
    }
    this.owner.window.contentView.addChildView(this.view);
    this.view.setVisible(true);this.update();if(!this.drag)this.view.webContents.focus();
  }
  update() {
    if(!this.selection || !this.view || this.view.webContents.isDestroyed())return;
    const version=bookmarkVersion(this.owner.config.WebBookmarks || []);
    if(version!==this.selection.version || !this.owner.presentation.showBookmarkBar){this.close();return;}
    const [width,height]=this.owner.window.getContentSize();
    const bottom=this.owner.presentation.showBookmarkBar ? 34:0;
    const top=34+(this.owner.addressBarVisible?34:0)+(this.owner.config.WebAlwaysShowTabs || this.owner.tabs.length>1?34:0);
    this.view.setBounds({x:0,y:top,width,height:Math.max(1,height-top-bottom)});
    this.view.webContents.send('bookmark-overlay-state',{...this.selection,theme:this.owner.theme,bookmarks:this.owner.bookmarks(),editable:this.owner.canEditBookmarks(),height:Math.max(1,height-top-bottom)});
  }
  close() {
    this.selection=null;
    clearTimeout(this.dragEndTimer);
    if(this.view && !this.view.webContents.isDestroyed())this.view.setVisible(false);
    if(this.ticket){this.owner.endDialog(this.ticket);this.ticket=null;}
    if(this.drag){this.drag=null;this.broadcastDrag();}
  }
  dispose() {
    this.close();
    if(this.view && !this.view.webContents.isDestroyed())this.view.webContents.close();
    this.view=null;
  }
}
module.exports={BookmarkOverlay};
