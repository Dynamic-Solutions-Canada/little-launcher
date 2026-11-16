'use strict';
for(const id of ['fullscreen-maximize','exit-fullscreen'])document.getElementById(id).addEventListener('click',()=>window.launcher.action(id));
window.launcher.onFullscreen(state=>{
 for(const [name,value] of Object.entries(state.theme))document.documentElement.style.setProperty('--'+name,value);
 const maximize=document.getElementById('fullscreen-maximize');maximize.hidden=!state.contained;maximize.textContent=state.strings.maximize;maximize.title=state.strings.maximizeTitle;
 const exit=document.getElementById('exit-fullscreen');exit.textContent=state.strings.exit;exit.title=state.strings.exitTitle;
});
