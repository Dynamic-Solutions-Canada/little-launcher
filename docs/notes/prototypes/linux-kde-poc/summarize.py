"""Summarize target-specific painted bounds so a previous flyout cannot count as first visibility."""
import json, pathlib
root=pathlib.Path('/work')
results=[]
for folder in sorted((root/'frames').iterdir()):
    observations=folder/'observations.json'
    if not observations.exists(): continue
    rows=json.loads(observations.read_text())
    target='web' if 'web' in folder.name else 'shortcuts'
    visible=[row for row in rows if row['bounds'][target]]
    bounds=sorted({tuple(row['bounds'][target]) for row in visible})
    result={'experiment':folder.name,'target':target,'sampleCount':len(rows),'firstTargetVisible':visible[0] if visible else None,'uniqueTargetBounds':bounds,'lastSampleSeconds':rows[-1]['elapsedSeconds']}
    (root/(folder.name+'-summary.json')).write_text(json.dumps(result,indent=2)+'\n')
    results.append(result)
(root/'results.json').write_text(json.dumps(results,indent=2)+'\n')
print(json.dumps([{'experiment':r['experiment'],'firstSeconds':r['firstTargetVisible']['elapsedSeconds'] if r['firstTargetVisible'] else None,'bounds':r['uniqueTargetBounds'],'samples':r['sampleCount']} for r in results],indent=2))
