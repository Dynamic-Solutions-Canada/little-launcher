'use strict';
// Static vector outlines avoid platform-dependent text glyph sizes in the shared header slots.
const paths = {
  back: 'M10 3 5 8l5 5', forward: 'm6 3 5 5-5 5', reload: 'M12 5a5 5 0 1 0 1 5M12 1v4H8',
  home: 'm2 7 6-5 6 5M4 6v8h8V6M7 14v-4h2v4', more: 'M3 8h.1M8 8h.1M13 8h.1',
  extensions: 'M6 4V3a2 2 0 0 1 4 0v1h3v3h.5a1.5 1.5 0 0 1 0 3H13v4H9v-1a2 2 0 0 0-4 0v1H2v-4h1a2 2 0 0 0 0-4H2V4z',
  address: 'M6 10 10 6M5 7 3 9a3 3 0 0 0 4 4l2-2M7 5l2-2a3 3 0 0 1 4 4l-2 2',
  pin: 'm9 2 5 5-3 1-2 4-2-2-5 5 5-5-2-2 4-2z', minimize: 'M3 8h10',
  maximize: 'M3 3h10v10H3z', restore: 'M5 5V2h9v9h-3M2 5h9v9H2z',
  hide: 'm4 4 8 8M12 4l-8 8', 'new-tab': 'M8 2v12M2 8h12',
  folder: 'M1 4h5l2 2h7v8H1zM1 4V2h5l2 2',
  globe: 'M14 8A6 6 0 1 1 2 8a6 6 0 0 1 12 0M2 8h12M8 2c-4 4-4 8 0 12 4-4 4-8 0-12',
  'bookmark-add': 'm8 1 2 5 5 .5-4 3 1 5L8 12l-4 2.5 1-5-4-3L6 6z',
};
function icon(button, name) {
  button.replaceChildren();
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16'); svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(svg.namespaceURI, 'path'); path.setAttribute('d', paths[name]);
  svg.append(path); button.append(svg);
}
for (const action of Object.keys(paths).filter(key => !['restore', 'folder', 'globe'].includes(key))) {
  const button = document.getElementById(action);
  icon(button, action); button.setAttribute('aria-label', button.title);
  button.addEventListener('click', () => window.launcher.action(action));
}
const url = document.getElementById('url');
document.getElementById('address-form').addEventListener('submit', event => {
  event.preventDefault();
  let address = url.value.trim();
  if (!/^[a-z][a-z0-9+.-]*:/i.test(address)) address = 'https://' + address;
  window.launcher.action('navigate', address);
});
let focusAddress = false;
window.launcher.onFocusAddress(() => { focusAddress = true; url.focus(); url.select(); });
document.getElementById('tabs').addEventListener('contextmenu', event => {
  event.preventDefault(); window.launcher.action('tab-context', null);
});
let stripDrag = null, pendingState = null;
const bookmarkDrag = new BookmarkDrag(()=>{if(!bookmarkDrag.session && pendingState){const state=pendingState;pendingState=null;renderState(state);}});
function finishStripDrag() {
  stripDrag = null;
  document.querySelectorAll('.drop-before,.drop-after,.drop-inside').forEach(item=>item.classList.remove('drop-before','drop-after','drop-inside'));
  if (pendingState) {const state=pendingState;pendingState=null;renderState(state);}
}
function wireStripDrag(button, kind, source, folder = false) {
  button.draggable = true;
  button.addEventListener('dragstart', event=>{
    stripDrag={kind,source,version:bookmarkVersion};
    event.dataTransfer.effectAllowed='move';event.dataTransfer.setData('application/x-little-launcher-strip','internal');
  });
  button.addEventListener('dragend', finishStripDrag);
  button.addEventListener('dragover', event=>{
    if (stripDrag?.kind!==kind) return;
    event.preventDefault();event.stopPropagation();event.dataTransfer.dropEffect='move';
    const bounds=button.getBoundingClientRect(), fraction=(event.clientX-bounds.left)/bounds.width;
    const inside=kind==='bookmark' && folder && fraction>.2 && fraction<.8;
    button.classList.toggle('drop-inside',inside);button.classList.toggle('drop-before',!inside && fraction<.5);button.classList.toggle('drop-after',!inside && fraction>=.5);
  });
  button.addEventListener('dragleave',()=>button.classList.remove('drop-before','drop-after','drop-inside'));
  button.addEventListener('drop',event=>{
    if (stripDrag?.kind!==kind) return;
    event.preventDefault();event.stopPropagation();
    const bounds=button.getBoundingClientRect(), fraction=(event.clientX-bounds.left)/bounds.width;
    window.launcher.action(kind==='tab'?'move-tab':'move-bookmark',{source:stripDrag.source,target:source,
      version:stripDrag.version,after:fraction>=.5,inside:kind==='bookmark' && folder && fraction>.2 && fraction<.8});
    finishStripDrag();
  });
}
const bookmarkStrip=document.getElementById('bookmarks');
bookmarkStrip.addEventListener('dragover',event=>{if(bookmarkDrag.session){event.preventDefault();event.dataTransfer.dropEffect='move';}});
bookmarkStrip.addEventListener('drop',event=>{
  if (!bookmarkDrag.accept(event)) return;
  event.preventDefault();bookmarkDrag.drop(-1,true,false);bookmarkDrag.finish();
});
let bookmarkVersion = '';
let overflowFrom = 0;
function layoutBookmarks() {
  const bar = document.getElementById('bookmarks');
  const overflow = bar.querySelector('.bookmark-overflow');
  if (!overflow || bar.hidden) return;
  const buttons = [...bar.querySelectorAll('[data-bookmark-index]')];
  for (const button of buttons) button.hidden = false;
  overflow.hidden = true;
  bar.classList.remove('overflowing');
  const add = bar.lastElementChild;
  const styles = getComputedStyle(bar);
  const gap = parseFloat(styles.columnGap) || 0;
  const padding = parseFloat(styles.paddingLeft) + parseFloat(styles.paddingRight);
  const total = padding + add.getBoundingClientRect().width + buttons.reduce((sum, button) => sum + button.getBoundingClientRect().width + gap, 0);
  if (total <= bar.clientWidth) return;
  bar.classList.add('overflowing');
  overflow.hidden = false;
  let used = padding + overflow.getBoundingClientRect().width + add.getBoundingClientRect().width + gap;
  overflowFrom = buttons.length;
  for (const [index, button] of buttons.entries()) {
    used += button.getBoundingClientRect().width + gap;
    if (used > bar.clientWidth || overflowFrom < buttons.length) {
      overflowFrom = Math.min(overflowFrom, index); button.hidden = true;
    }
  }
}
new ResizeObserver(layoutBookmarks).observe(document.getElementById('bookmarks'));
document.getElementById('bookmarks').addEventListener('contextmenu', event => {
  event.preventDefault(); window.launcher.action('bookmark-context', {index: -1, version: bookmarkVersion});
});
function renderState(state) {
  const extensions=state.extensions||[];
  document.getElementById('extensions').hidden=!extensions.length;
  const pinned=document.getElementById('pinned-extensions');pinned.replaceChildren();
  for(const extension of extensions.filter(item=>item.pinned&&item.popup&&item.running)){
    const button=document.createElement('button');button.className='extension-action';button.dataset.extensionId=extension.id;
    button.title=extension.name;button.setAttribute('aria-label',extension.name);
    if(extension.icon?.startsWith('data:image/png;base64,')){
      const image=document.createElement('img');image.src=extension.icon;image.alt='';button.append(image);
    }else icon(button,'extensions');
    if(extension.badge){const badge=document.createElement('span');badge.className='extension-badge';badge.textContent=extension.badge;button.append(badge);}
    button.onclick=()=>window.launcher.action('extension-open',extension.id);
    button.oncontextmenu=event=>{event.preventDefault();window.launcher.action('extension-context',extension.id);};
    pinned.append(button);
  }
  bookmarkVersion = state.bookmarkVersion;
  document.getElementById('title').textContent = state.name;
  for (const name of ['window', 'text', 'button', 'highlight', 'font']) {
    document.documentElement.style.setProperty('--' + name, state.theme[name]);
  }
  document.getElementById('back').disabled = !state.canGoBack;
  document.getElementById('forward').disabled = !state.canGoForward;
  document.getElementById('reload').title = state.loading ? 'Reload (loading)' : 'Reload';
  document.getElementById('pin').setAttribute('aria-pressed', String(state.pinned));
  document.getElementById('address').setAttribute('aria-pressed', String(state.showAddressBar));
  for (const name of ['title', 'minimize', 'maximize', 'hide']) document.getElementById(name).hidden = !!state.nativeFrame;
  document.querySelector('header').classList.toggle('native-frame', !!state.nativeFrame);
  icon(document.getElementById('maximize'), state.maximized ? 'restore' : 'maximize');
  document.getElementById('address-form').hidden = !state.showAddressBar;
  if (focusAddress && state.showAddressBar) { url.focus(); url.select(); focusAddress = false; }
  if (document.activeElement !== url) url.value = state.address;
  document.getElementById('pin').disabled = state.pinSupported === false;
  document.getElementById('bookmark-add').disabled = !state.editable;
  document.getElementById('tabs').hidden = !state.showTabs;
  const list = document.getElementById('tab-list'); list.replaceChildren();
  for (const tab of state.tabs) {
    const cell = document.createElement('div'); cell.className = 'tab';
    const select = document.createElement('button'); select.dataset.tabId=String(tab.id); select.setAttribute('role', 'tab');
    select.setAttribute('aria-selected', String(tab.active)); select.title = tab.title;
    const caption = document.createElement('span');caption.className='tab-caption';caption.textContent=tab.title;select.append(caption);
    wireStripDrag(select,'tab',tab.id);
    select.addEventListener('click', () => window.launcher.action('select-tab', tab.id));
    select.addEventListener('auxclick', event => { if (event.button === 1) window.launcher.action('close-tab', tab.id); });
    const close = document.createElement('button'); close.className = 'tab-close';
    close.title = 'Close tab'; close.setAttribute('aria-label', 'Close ' + tab.title); icon(close, 'hide');
    close.addEventListener('click', () => window.launcher.action('close-tab', tab.id));
    cell.addEventListener('contextmenu', event => {
      event.preventDefault(); event.stopPropagation(); window.launcher.action('tab-context', tab.id);
    });
    const slot = document.createElement('span'); slot.className = 'tab-icon';slot.setAttribute('aria-hidden','true');
    if (tab.favicon) {const img = document.createElement('img');img.src=tab.favicon;img.alt='';slot.append(img);}
    else icon(slot, 'globe');
    select.prepend(slot);
    cell.append(select, close); list.append(cell);
  }
  const bar = document.getElementById('bookmarks'); bar.hidden = !state.showBookmarkBar; bar.replaceChildren();
  for (const [index, bookmark] of state.bookmarks.entries()) {
    const button = document.createElement('button');
    button.dataset.bookmarkIndex = String(index);
    const open=()=>{const rect=button.getBoundingClientRect();window.launcher.action('bookmark-menu',{path:bookmark.sourcePath,version:state.bookmarkVersion,anchor:{x:rect.left,y:rect.top}});};
    if(state.editable)bookmarkDrag.wire(button,bookmark.sourcePath,!!bookmark.IsFolder,state.bookmarkVersion,false,open);
    const caption = bookmark.Name || bookmark.Url;
    const iconsOnly = state.bookmarkIconsOnly || bookmark.IconsOnly;
    button.className = 'bookmark' + (iconsOnly ? ' icons-only' : '');
    icon(button, bookmark.IsFolder ? 'folder' : 'globe');
    if (bookmark.favicon) {
      const img = document.createElement('img'); img.src = bookmark.favicon; img.alt = '';
      button.replaceChildren(img);
    }
    const label = document.createElement('span'); label.textContent = caption; label.hidden = !!iconsOnly;
    button.append(label);
    button.title = bookmark.IsFolder ? caption : caption + '\n' + bookmark.Url;
    button.setAttribute('aria-label', caption);
    button.addEventListener('click', event => {if(bookmark.IsFolder)open();else window.launcher.action('bookmark-open',
      {index, version: state.bookmarkVersion, newTab: event.ctrlKey || event.shiftKey || event.metaKey});});
    button.addEventListener('auxclick', event => {
      if (event.button !== 1 || bookmark.IsFolder) return;
      event.preventDefault(); window.launcher.action('bookmark-open', {index,version:state.bookmarkVersion,newTab:true});
    });
    button.addEventListener('contextmenu', event => {
      event.preventDefault(); event.stopPropagation();
      window.launcher.action('bookmark-context', {index, version: state.bookmarkVersion});
    });
    bar.append(button);
  }
  const overflow = document.createElement('button'); overflow.className = 'bookmark-overflow';
  overflow.textContent = '»'; overflow.title = 'More bookmarks'; overflow.setAttribute('aria-label', overflow.title);
  const openOverflow=()=>{const rect=overflow.getBoundingClientRect();window.launcher.action('bookmark-overflow',{index:overflowFrom,version:bookmarkVersion,anchor:{x:rect.left,y:rect.top}});};
  overflow.addEventListener('click',openOverflow);
  let overflowTimer;overflow.addEventListener('dragover',event=>{if(bookmarkDrag.session){event.preventDefault();if(!overflowTimer)overflowTimer=setTimeout(()=>{overflowTimer=null;openOverflow();},400);}});
  overflow.addEventListener('dragleave',()=>{clearTimeout(overflowTimer);overflowTimer=null;});
  bar.append(overflow);
  const add = document.createElement('button'); icon(add, 'new-tab');
  add.title = 'Add current page to bookmarks'; add.setAttribute('aria-label', add.title); add.disabled = !state.editable;
  add.addEventListener('click', () => window.launcher.action('bookmark-add')); bar.append(add);
  layoutBookmarks();
}
document.addEventListener('pointerdown',event=>{if(!event.target.closest('.bookmark,.bookmark-overflow'))window.launcher.action('bookmark-overlay-close');});
window.launcher.onState(state=>{if(stripDrag || bookmarkDrag.session)pendingState=state;else renderState(state);});
