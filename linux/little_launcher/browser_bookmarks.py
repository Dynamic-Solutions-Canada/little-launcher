"""Read-only browser bookmark discovery and bounded import projections; no passwords or cookies."""
from contextlib import closing
from dataclasses import dataclass
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import sqlite3
from .document import DocumentError, normalize_web_address

MAX_BOOKMARKS=10000
MAX_FILE=32*1024*1024

@dataclass(frozen=True)
class BookmarkSource:
    browser: str
    profile: str
    path: Path


def sources(home=None):
    home=Path(home) if home is not None else Path.home()
    config=Path(os.environ.get('XDG_CONFIG_HOME',home/'.config')) if home==Path.home() else home/'.config'
    result=[]
    for name,directory in [('Chrome',config/'google-chrome'),('Chromium',config/'chromium'),
                           ('Edge',config/'microsoft-edge'),('Brave',config/'BraveSoftware/Brave-Browser'),
                           ('Vivaldi',config/'vivaldi'),('Chromium (Flatpak)',home/'.var/app/org.chromium.Chromium/config/chromium')]:
        if not directory.is_dir():continue
        try:profiles=sorted(directory.iterdir())[:128]
        except OSError:continue
        for profile in profiles:
            path=profile/'Bookmarks'
            if path.is_file():result.append(BookmarkSource(name,profile.name,path))
    for browser,directory in [('Firefox',home/'.mozilla/firefox'),('Firefox (Flatpak)',home/'.var/app/org.mozilla.firefox/.mozilla/firefox'),('Zen',home/'.zen'),('Waterfox',home/'.waterfox'),('LibreWolf',home/'.librewolf'),('LibreWolf (Flatpak)',home/'.var/app/io.gitlab.librewolf-community/.librewolf')]:
        if directory.is_dir():
            try:profiles=sorted(directory.iterdir())[:128]
            except OSError:continue
            for profile in profiles:
                path=profile/'places.sqlite'
                if path.is_file():result.append(BookmarkSource(browser,profile.name,path))
    return result


def read_bookmarks(path):
    path=Path(path)
    result=[]
    def add(name,address,folder=''):
        if not isinstance(address,str) or len(address)>8192:return
        try:address=normalize_web_address(address)
        except (DocumentError,TypeError,AttributeError):return
        result.append({'Name':str(name or address)[:1024],'Url':address,'Folder':folder})
        if len(result)>MAX_BOOKMARKS:raise DocumentError('This bookmark source exceeds the 10,000-entry import limit.')
    try:
        if path.suffix.lower() in ('.sqlite','.db'):
            with closing(sqlite3.connect(path.resolve().as_uri()+'?mode=ro',uri=True,timeout=.25)) as database:
                database.execute('PRAGMA query_only=ON')
                database.execute('BEGIN')
                columns={row[1] for row in database.execute('PRAGMA table_info(moz_bookmarks)')}
                folders={row[0]:(row[1],row[2]) for row in database.execute('SELECT id,substr(title,1,1024),parent FROM moz_bookmarks WHERE type=2 LIMIT 10001')} if 'id' in columns else {}
                if len(folders)>MAX_BOOKMARKS:raise DocumentError('This bookmark source has too many folders to import.')
                def folder_path(parent):
                    names=[];seen=set()
                    while parent in folders:
                        if parent in seen or len(seen)>=32:raise DocumentError('This bookmark source contains an invalid or overly deep folder tree.')
                        seen.add(parent);name,parent=folders[parent]
                        if name:names.append(str(name))
                    return '/'.join(reversed(names))
                for name,address,parent in database.execute('SELECT substr(b.title,1,1024),substr(p.url,1,8193),b.parent FROM moz_bookmarks b JOIN moz_places p ON p.id=b.fk WHERE b.type=1 ORDER BY b.parent,b.position LIMIT 10001'):
                    add(name,address,folder_path(parent))
        else:
            if path.stat().st_size>MAX_FILE:raise DocumentError('This bookmark source is too large to import.')
            raw=path.read_text(encoding='utf-8-sig')
            if path.suffix.lower() in ('.html','.htm'):
                class Parser(HTMLParser):
                    address=None
                    label=''
                    heading=None
                    pending=None
                    folders=[]
                    levels=[]
                    def handle_starttag(self,tag,attrs):
                        if tag=='a':self.address=dict(attrs).get('href');self.label=''
                        elif tag=='h3':self.heading=''
                        elif tag=='dl':
                            pushed=self.pending is not None;self.levels.append(pushed)
                            if pushed:
                                self.folders.append(self.pending);self.pending=None
                                if len(self.folders)>32:raise DocumentError('This bookmark source has too many folder levels.')
                    def handle_data(self,text):
                        if self.address is not None:self.label+=text
                        elif self.heading is not None:self.heading+=text
                    def handle_endtag(self,tag):
                        if tag=='a' and self.address is not None:
                            add(self.label,self.address,'/'.join(self.folders));self.address=None
                        elif tag=='h3':self.pending=self.heading;self.heading=None
                        elif tag=='dl' and self.levels:
                            if self.levels.pop():self.folders.pop()
                parser=Parser(convert_charrefs=True);parser.feed(raw)
            else:
                data=json.loads(raw)
                def walk(item,folder='',depth=0):
                    if not isinstance(item,dict) or depth>32:return
                    if item.get('type')=='url':add(item.get('name'),item.get('url'),folder)
                    elif item.get('type')=='folder':
                        name=item.get('name','');prefix='/'.join(part for part in [folder,str(name)] if part)
                        for child in item.get('children',[]):walk(child,prefix,depth+1)
                for root in data.get('roots',{}).values():walk(root)
    except DocumentError:
        raise
    except (OSError,ValueError,sqlite3.Error,TypeError,AttributeError):
        raise DocumentError('This browser bookmark source could not be read. Close the browser or choose an exported bookmark file.') from None
    return result
