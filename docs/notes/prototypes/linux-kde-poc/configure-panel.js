// Deterministic private test panel. Never run against the user's live Plasma session.
var previous=panels();
for(var i=0;i<previous.length;i++) previous[i].remove();
var panel=new Panel;
panel.location='bottom';
panel.height=48;
panel.addWidget('org.kde.plasma.panelspacer');
var tray=panel.addWidget('org.kde.plasma.systemtray');
tray.currentConfigGroup=['General'];
tray.writeConfig('showAllItems',true);
print('PRIVATE_PROBE_PANEL='+panel.id);
