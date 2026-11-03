'use strict';
for(const id of ['fullscreen-maximize','exit-fullscreen'])document.getElementById(id).addEventListener('click',()=>window.launcher.action(id));
window.launcher.onFullscreen(state=>{
 for(const [name,value] of Object.entries(state.theme))document.documentElement.style.setProperty('--'+name,value);
 const maximize=document.getElementById('fullscreen-maximize');maximize.hidden=!state.contained;maximize.textContent=state.maximized?'Restore':'Maximize';maximize.title=state.maximized?'Restore launcher size':'Maximize launcher';
});
