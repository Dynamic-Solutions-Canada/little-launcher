document.getElementById('files')?.addEventListener('click', () => window.probe.file());
document.getElementById('hide')?.addEventListener('click', () => window.probe.hide());
document.getElementById('quit')?.addEventListener('click', () => window.probe.quit());
document.addEventListener('keydown', event => {if (event.key === 'Escape') window.probe.hide();});
