'use strict';
/** Shared by two trusted local surfaces. A marker alone never establishes drag ownership. */
class BookmarkDrag {
  constructor(changed=()=>{}) {
    this.session=null;this.changed=changed;this.hoverTimer=null;
    window.launcher.onBookmarkDrag(session=>{this.session=session;this.changed();});
  }
  wire(button,source,folder,version,vertical=false,open=()=>{}) {
    button.draggable=true;
    button.addEventListener('dragstart',event=>{
      const token=crypto.randomUUID();this.session={source,version,token};this.changed();
      event.dataTransfer.effectAllowed='move';event.dataTransfer.setData('application/x-little-launcher-bookmark',token);
      window.launcher.action('bookmark-drag-start',this.session);
    });
    button.addEventListener('dragend',()=>this.finish());
    button.addEventListener('dragover',event=>{
      if(!this.session)return;
      event.preventDefault();event.stopPropagation();event.dataTransfer.dropEffect='move';
      const bounds=button.getBoundingClientRect(),fraction=vertical?(event.clientY-bounds.top)/bounds.height:(event.clientX-bounds.left)/bounds.width;
      const inside=folder && fraction>.2 && fraction<.8;
      button.classList.toggle('drop-inside',inside);button.classList.toggle('drop-before',!inside && fraction<.5);button.classList.toggle('drop-after',!inside && fraction>=.5);
      if(inside && !this.hoverTimer)this.hoverTimer=setTimeout(()=>{this.hoverTimer=null;open();},400);
      if(!inside)this.clearHover();
    });
    button.addEventListener('dragleave',()=>{this.clearHover();button.classList.remove('drop-before','drop-after','drop-inside');});
    button.addEventListener('drop',event=>{
      if(!this.accept(event))return;
      event.preventDefault();event.stopPropagation();
      const bounds=button.getBoundingClientRect(),fraction=vertical?(event.clientY-bounds.top)/bounds.height:(event.clientX-bounds.left)/bounds.width;
      this.drop(source,fraction>=.5,folder && fraction>.2 && fraction<.8);this.finish();
    });
  }
  accept(event) {return this.session && event.dataTransfer.getData('application/x-little-launcher-bookmark')===this.session.token;}
  drop(target,after,inside) {window.launcher.action('move-bookmark',{...this.session,target,after,inside});}
  clearHover() {clearTimeout(this.hoverTimer);this.hoverTimer=null;}
  finish() {
    this.clearHover();
    if(this.session)window.launcher.action('bookmark-drag-end',this.session.token);
    this.session=null;document.querySelectorAll('.drop-before,.drop-after,.drop-inside').forEach(item=>item.classList.remove('drop-before','drop-after','drop-inside'));this.changed();
  }
}
