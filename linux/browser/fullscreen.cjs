'use strict';
const {WebContentsView}=require('electron'),path=require('node:path');
const {fitVideoSize}=require('./fullscreen_geometry.cjs');
const i18n=require('./i18n.cjs');
const videoRatio=`(()=>{const root=document.fullscreenElement;if(!root)return null;const videos=root.matches('video')?[root]:[...root.querySelectorAll('video')];const visible=videos.filter(video=>video.videoWidth>0&&video.videoHeight>0&&video.getBoundingClientRect().width>0&&video.getBoundingClientRect().height>0);visible.sort((a,b)=>{const x=a.getBoundingClientRect(),y=b.getBoundingClientRect();return y.width*y.height-x.width*x.height});return visible.length?visible[0].videoWidth/visible[0].videoHeight:null})()`;

/**
 * Width for the controls strip, so translated labels are not clipped.
 * Estimated from the labels rather than measured in the page, which would need a new IPC message
 * from trusted chrome: about 8px per Latin character and 15px per wide (CJK) character, plus each
 * button's padding. Never narrower than the original 220px, nor wider than 480px.
 * @param {string[]} labels the visible button labels
 * @returns {number} strip width in DIPs
 */
function stripWidth(labels) {
  let text=0;
  for(const label of labels) {
    for(const character of label)text+=/[\u1100-\uffdc]/u.test(character)?15:8;
  }
  return Math.max(220,Math.min(480,text+labels.length*28+12));
}

/** Host-owned fullscreen presentation; no preload or controls are injected into the website. */
class PageFullscreen {
  constructor(owner) {this.owner=owner;this.state=null;this.version=0;owner.window.once('closed',()=>this.dispose());}
  enter(tab) {
    if(tab!==this.owner.activeTab || this.state)return;
    const owner=this.owner;owner.bookmarkOverlay.close();
    this.state={tab,contained:!!owner.config.WebFullScreenInWindow,size:owner.window.getContentSize(),maximized:owner.window.isMaximized()};
    this.version++;clearTimeout(owner.sizeTimer);
    if(!this.state.contained)owner.window.setFullScreen(true);
    if(!this.view) {
      this.view=new WebContentsView({webPreferences:{preload:path.join(__dirname,'preload.cjs'),sandbox:true,contextIsolation:true,nodeIntegration:false}});
      this.view.webContents.setWindowOpenHandler(()=>({action:'deny'}));
      this.view.webContents.on('will-navigate',event=>event.preventDefault());
      this.view.webContents.on('did-finish-load',()=>{this.view.webContents.setZoomMode('disabled');this.layout();});
      this.view.webContents.on('before-input-event',(event,input)=>{if(input.type==='keyDown'&&input.key==='Escape'){event.preventDefault();this.exitPage();}});
      void this.view.webContents.loadFile(path.join(__dirname,'fullscreen.html'));
    }
    owner.window.contentView.addChildView(this.view);this.view.setVisible(true);owner.layout();
    void this.fit(this.version);
  }
  async fit(version) {
    const state=this.state,owner=this.owner;
    const wanted=()=>this.state===state && version===this.version && state?.contained && owner.config.WebFitFullscreenToVideo && owner.window.isVisible() && !owner.window.isMaximized() && state.tab===owner.activeTab && state.tab.view && !state.tab.view.webContents.isDestroyed();
    try {
      for(let attempt=0;attempt<10 && wanted();attempt++) {
        const ratio=await state.tab.view.webContents.executeJavaScript(videoRatio);
        if(!wanted())return;
        if(typeof ratio==='number' && Number.isFinite(ratio) && ratio>0) {
          const area=owner.displayForAnchor(owner.anchor).workAreaSize;
          const outer=owner.window.getSize(),inner=owner.window.getContentSize();
          const size=fitVideoSize(inner[0],ratio,{width:area.width-(outer[0]-inner[0])-16,height:area.height-(outer[1]-inner[1])-16});
          if(size)owner.window.setContentSize(size.width,size.height);
          return;
        }
        await new Promise(resolve=>setTimeout(resolve,150));
      }
    }catch{/* A navigation or disappearing fullscreen element simply cancels fitting. */}
  }
  layout() {
    if(!this.state || !this.view || this.view.webContents.isDestroyed())return;
    const [width]=this.owner.window.getContentSize();
    const maximized=this.owner.window.isMaximized();
    // Resolved labels only; this trusted local page never reads the catalogs itself.
    const strings=i18n.strings({maximize:maximized?'web.restoreSize':'web.maximize',maximizeTitle:maximized?'webFullscreen.restoreLauncherSize':'webFullscreen.maximizeLauncher',
      exit:'webFullscreen.exitButton',exitTitle:'webFullscreen.exitWithEscape'});
    const strip=Math.min(width,stripWidth(this.state.contained?[strings.maximize,strings.exit]:[strings.exit]));
    this.view.setBounds({x:Math.max(0,width-strip),y:0,width:strip,height:34});
    this.view.webContents.send('fullscreen-state',{theme:this.owner.theme,contained:this.state.contained,maximized,strings});
  }
  action(action) {
    if(action==='exit-fullscreen'){this.exitPage();return true;}
    if(action==='fullscreen-maximize') {
      if(this.state?.contained){this.version++;this.owner.window.isMaximized()?this.owner.window.unmaximize():this.owner.window.maximize();}
      return true;
    }
    return false;
  }
  exitPage() {
    const contents=this.state?.tab.view?.webContents;
    if(contents && !contents.isDestroyed())void contents.executeJavaScript('document.fullscreenElement && document.exitFullscreen()').then(()=>this.leave()).catch(()=>this.leave());
    else this.leave();
  }
  leave(tab=this.state?.tab) {
    if(!this.state || this.state.tab!==tab)return;
    const state=this.state;this.version++;
    if(this.view && !this.view.webContents.isDestroyed())this.view.setVisible(false);
    const owner=this.owner;
    if(!owner.window.isDestroyed()) {
      if(!state.contained)owner.window.setFullScreen(false);
      if(owner.window.isMaximized() && !state.maximized)owner.window.unmaximize();
      if(!state.maximized)owner.window.setContentSize(...state.size);
    }
    this.state=null;
    // Geometry acknowledgements must settle before ordinary size persistence resumes.
    owner.applyingSize=true;setTimeout(()=>{owner.applyingSize=false;},350);
    if(!owner.window.isDestroyed()){owner.layout();owner.toolbar();}
  }
  dispose() {this.version++;this.state=null;if(this.view&&!this.view.webContents.isDestroyed())this.view.webContents.close();this.view=null;}
}
module.exports={PageFullscreen};
