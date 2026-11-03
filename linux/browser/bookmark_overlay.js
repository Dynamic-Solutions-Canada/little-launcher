'use strict';
const panels=document.getElementById('panels');let current=null,pending=null,openPaths=[];
const drag=new BookmarkDrag(()=>{if(!drag.session && pending){const state=pending;pending=null;render(state);}});
function same(a,b){return JSON.stringify(a)===JSON.stringify(b);}
function find(path) {
  let items=current.bookmarks;
  for(let depth=0;depth<path.length;depth++) {
    const item=items.find(value=>same(value.sourcePath,path.slice(0,depth+1)));
    if(!item)return null;if(depth===path.length-1)return item;items=item.Children || [];
  }
}
function graphic(button,item) {
  if(item.favicon){const image=document.createElement('img');image.src=item.favicon;image.alt='';button.append(image);return;}
  const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 16 16');svg.setAttribute('aria-hidden','true');
  const path=document.createElementNS(svg.namespaceURI,'path');path.setAttribute('d',item.IsFolder?'M1 4h5l2 2h7v8H1zM1 4V2h5l2 2':'M14 8A6 6 0 1 1 2 8a6 6 0 0 1 12 0M2 8h12M8 2c-4 4-4 8 0 12 4-4 4-8 0-12');svg.append(path);button.append(svg);
}
function show(items,folder,anchor,depth=0) {
  for(const panel of [...panels.children].slice(depth))panel.remove();openPaths=openPaths.slice(0,depth);
  openPaths.push(folder);
  const panel=document.createElement('section');panel.className='bookmark-panel';panel.setAttribute('aria-label',folder?find(folder)?.Name || 'Folder':'More bookmarks');panel.dataset.depth=depth;
  if(folder)panel.dataset.folder=JSON.stringify(folder);
  for(const item of items) {
    const row=document.createElement('button');row.className='bookmark-row';row.dataset.path=JSON.stringify(item.sourcePath);row.title=item.Url || item.Name || 'Folder';graphic(row,item);
    const label=document.createElement('span');label.textContent=item.Name || item.Url || 'Folder';row.append(label);
    if(item.IsFolder){const arrow=document.createElement('span');arrow.textContent='›';arrow.style.flex='0';row.append(arrow);}
    const expand=()=>{if(!same(openPaths[depth+1],item.sourcePath))show(item.Children,item.sourcePath,row,depth+1);};
    row.addEventListener('click',event=>{
      if(item.IsFolder){if(same(openPaths[depth+1],item.sourcePath)){for(const value of [...panels.children].slice(depth+1))value.remove();openPaths=openPaths.slice(0,depth+1);}else expand();}
      else {window.launcher.action('bookmark-open',{path:item.sourcePath,version:current.version,newTab:event.ctrlKey||event.shiftKey||event.metaKey});window.launcher.action('bookmark-overlay-close');}
    });
    row.addEventListener('auxclick',event=>{if(event.button===1 && !item.IsFolder){event.preventDefault();window.launcher.action('bookmark-open',{path:item.sourcePath,version:current.version,newTab:true});}});
    row.addEventListener('contextmenu',event=>{event.preventDefault();event.stopPropagation();window.launcher.action('bookmark-context',{path:item.sourcePath,version:current.version});});
    if(current.editable)drag.wire(row,item.sourcePath,!!item.IsFolder,current.version,true,expand);
    panel.append(row);
  }
  if(!items.length){const empty=document.createElement('div');empty.className='empty-folder';empty.textContent='Empty folder';panel.append(empty);}
  panel.addEventListener('contextmenu',event=>{event.preventDefault();window.launcher.action('bookmark-context',{...(folder?{path:folder}:{index:-1}),version:current.version});});
  panel.addEventListener('dragover',event=>{if(drag.session){event.preventDefault();event.dataTransfer.dropEffect='move';}});
  panel.addEventListener('drop',event=>{if(drag.accept(event)){event.preventDefault();drag.drop(folder || -1,true,!!folder);drag.finish();}});
  panels.append(panel);
  const bounds=anchor?.getBoundingClientRect?.();
  const x=depth?bounds.right-32:current.anchor.x;
  const y=depth?bounds.top-9:panels.clientHeight-panel.offsetHeight;
  panel.style.left=Math.max(0,Math.min(panels.clientWidth-panel.offsetWidth,x))+'px';
  panel.style.top=Math.max(0,Math.min(panels.clientHeight-panel.offsetHeight,y))+'px';
  if(!drag.session)panel.querySelector('button')?.focus();
}
function render(state) {
  current=state;for(const [name,value]of Object.entries(state.theme))document.documentElement.style.setProperty('--'+name,value);
  const paths=openPaths.slice(1);panels.replaceChildren();openPaths=[];
  const folder=state.folder && find(state.folder);
  show(folder?folder.Children:state.bookmarks.slice(state.overflow),state.folder,null);
  for(const path of paths){const parent=panels.lastChild;const anchor=[...parent.querySelectorAll('[data-path]')].find(row=>same(JSON.parse(row.dataset.path),path));const item=find(path);if(!anchor || !item?.IsFolder)break;show(item.Children,path,anchor,openPaths.length);}
}
panels.addEventListener('pointerdown',event=>{if(event.target===panels)window.launcher.action('bookmark-overlay-close');});
document.addEventListener('keydown',event=>{
  if(event.key==='Escape'){event.preventDefault();window.launcher.action('bookmark-overlay-close');}
  if(event.key==='ArrowRight'){const row=document.activeElement;if(row?.dataset.path && find(JSON.parse(row.dataset.path))?.IsFolder){event.preventDefault();row.click();}}
  if(event.key==='ArrowLeft' && panels.children.length>1){event.preventDefault();const path=openPaths.pop();panels.lastChild.remove();const parent=[...panels.lastChild.querySelectorAll('[data-path]')].find(row=>same(JSON.parse(row.dataset.path),path));parent?.focus();}
  if(event.key==='ArrowDown' || event.key==='ArrowUp'){const rows=[...document.activeElement.closest('.bookmark-panel')?.querySelectorAll('button') || []];const index=rows.indexOf(document.activeElement);if(rows.length){event.preventDefault();rows[(index+(event.key==='ArrowDown'?1:rows.length-1))%rows.length].focus();}}
});
window.launcher.onBookmarkOverlay(state=>{if(drag.session && current && same(current.folder,state.folder) && current.overflow===state.overflow && current.version===state.version)pending=state;else render(state);});
