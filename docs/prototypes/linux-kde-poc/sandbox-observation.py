"""Observe only this container's Electron process flags and Linux sandbox status, without reading environment values."""
import json,pathlib,re
rows=[]
for entry in pathlib.Path('/proc').iterdir():
    if not entry.name.isdigit(): continue
    try:
        args=(entry/'cmdline').read_bytes().decode(errors='replace').split('\0')
        if not args or not args[0].startswith('/opt/electron/'): continue
        status=(entry/'status').read_text().splitlines()
        command=' '.join(args)
        process_type=re.search(r'(?:^| )--type=([^ ]+)',command)
        selected={row.split(':',1)[0]:row.split(':',1)[1].strip() for row in status if row.startswith(('NoNewPrivs:','Seccomp:','Seccomp_filters:','CapEff:','NSpid:'))}
        rows.append({'pid':int(entry.name),'type':process_type.group(1) if process_type else 'main','noSandboxFlag':'--no-sandbox' in args,'status':selected})
    except (FileNotFoundError,PermissionError): continue
pathlib.Path('/work/sandbox-observation.json').write_text(json.dumps(rows,indent=2)+'\n')
print(json.dumps(rows,indent=2))
