"""Automatic, idempotent collection. Sources are always opened read-only."""
from __future__ import annotations
import contextlib,ctypes,datetime as dt,gzip,json,logging,os,pathlib,re,shutil,sqlite3,time
from common import *
from analyze import analyze

SESSION=re.compile(r'(?:^|-)([0-9a-f]{32})(?:\.part\d+)?\.jsonl$',re.I)

def game_running():
    if os.name!='nt':return False
    import ctypes.wintypes as w
    class Entry(ctypes.Structure):
        _fields_=[('size',w.DWORD),('usage',w.DWORD),('pid',w.DWORD),('heap',ctypes.c_size_t),
                  ('module',w.DWORD),('threads',w.DWORD),('parent',w.DWORD),('priority',w.LONG),
                  ('flags',w.DWORD),('name',w.WCHAR*260)]
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.CreateToolhelp32Snapshot.restype=w.HANDLE
    handle=k.CreateToolhelp32Snapshot(2,0)
    if handle==ctypes.c_void_p(-1).value:raise OSError('Cannot inspect game process')
    k.Process32FirstW.argtypes=[w.HANDLE,ctypes.POINTER(Entry)]
    k.Process32NextW.argtypes=[w.HANDLE,ctypes.POINTER(Entry)]
    k.CloseHandle.argtypes=[w.HANDLE]
    entry=Entry();entry.size=ctypes.sizeof(entry)
    try:
        ok=k.Process32FirstW(handle,ctypes.byref(entry))
        while ok:
            if entry.name.lower()=='escapefromtarkov.exe':return True
            ok=k.Process32NextW(handle,ctypes.byref(entry))
        return False
    finally:k.CloseHandle(handle)

def tail(path):
    with pathlib.Path(path).open('rb') as f:
        f.seek(max(0,f.seek(0,2)-8192));lines=f.read().splitlines()
    for line in reversed(lines):
        try:
            e=json.loads(line)
            if isinstance(e,dict) and e.get('stage') in ('diagnostics-end','performance-end'):return e
        except (ValueError,UnicodeError):pass
    return None

def active_raid(game):
    if not game_running():return False
    groups=sessions(game)
    latest=max(groups.values(),key=lambda ps:max(p.stat().st_mtime for p in ps),default=[])
    return not latest or not tail(latest[-1])

def sessions(game):
    result={}
    for p in (pathlib.Path(game)/'BepInEx/COMET/diagnostics').glob('*.jsonl'):
        m=SESSION.search(p.name)
        if m:result.setdefault(m[1].lower(),[]).append(p)
    # Primary file first, then numbered segments, independent of directory iteration order.
    for group in result.values():group.sort(key=lambda p:int(re.search(r'\.part(\d+)',p.name)[1]) if '.part' in p.name else 0)
    return result

def rr_connect(game):
    path=pathlib.Path(game)/'SPT_Runtime/user/mods/RaidReview/data/raid_review_mod.db'
    if not path.exists():return None
    c=sqlite3.connect(path.as_uri()+'?mode=ro',uri=True,timeout=2);c.row_factory=sqlite3.Row
    c.execute('PRAGMA query_only=ON');return c

def table_names(c):
    return [r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") if re.fullmatch(r'\w+',r[0])]

def rr_index(game):
    c=rr_connect(game)
    if c is None:return [],{}
    with contextlib.closing(c):
        tables=table_names(c)
        raids=[dict(r) for r in c.execute('SELECT * FROM raid ORDER BY id DESC')]
        links={}
        if 'comet_decision' in tables:
            for r in c.execute('SELECT DISTINCT cometSession,raidId FROM comet_decision'):
                links.setdefault(r[0],[]).append(r[1])
        return raids,links

def ensure(c,raid,meta):
    old=c.execute('SELECT metadata FROM raids WHERE id=?',(raid,)).fetchone()
    value=json.loads(old[0]) if old else {}
    value.update(meta)
    c.execute('INSERT INTO raids VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET updated=excluded.updated,metadata=excluded.metadata',
              (raid,utc(),dumps(value),'{}'))
    return value

def add_file(c,root,raid,kind,p,attribution='exact'):
    before=fingerprint(p)
    cached=c.execute('SELECT * FROM file_cache WHERE path=? AND fingerprint=?',(str(p),before)).fetchone()
    if cached and (pathlib.Path(root)/'blobs'/(cached['hash']+'.gz')).is_file():sha,size=cached['hash'],cached['size']
    else:
        sha,size=store_file(root,p)
        if before!=fingerprint(p):raise RuntimeError('Source changed during collection')
        c.execute('INSERT OR REPLACE INTO file_cache VALUES(?,?,?,?)',(str(p),before,sha,size))
    artifact(c,raid,kind,p,sha,size,attribution);return sha

def add_value(c,root,raid,kind,name,value,attribution='exact'):
    sha,size=store_value(root,value);artifact(c,raid,kind,name,sha,size,attribution)

def parse_diagnostic(c,root,raid,path):
    before=fingerprint(path)
    previous=c.execute('SELECT fingerprint FROM imports WHERE path=?',(str(path),)).fetchone()
    if previous and previous[0]==before:return False
    sha=add_file(c,root,raid,'comet',path)
    malformed=0;unsupported=0
    with read_blob(root,sha) as f:
        for line_no,line in enumerate(f,1):
            if len(line)>4*1024*1024:malformed+=1;continue
            try:
                e=json.loads(line)
                if not isinstance(e,dict):raise ValueError('Not object')
                if e.get('schema',1)!=1:unsupported+=1
                insert_event(c,raid,e,'blob:'+sha,line_no)
            except (ValueError,UnicodeError,RecursionError):malformed+=1
    c.execute('INSERT OR REPLACE INTO imports VALUES(?,?,?)',(str(path),before,raid))
    old=json.loads(c.execute('SELECT metadata FROM raids WHERE id=?',(raid,)).fetchone()[0])
    errors=old.get('sourceErrors',{});errors[str(path)]=malformed
    unknown=old.get('unsupportedSources',{});unknown[str(path)]=unsupported
    ensure(c,raid,{'sourceErrors':errors,'malformed':sum(errors.values()),'unsupportedSources':unknown,'unsupportedSchemas':sum(unknown.values())})
    return True

def capture_rr(c,root,game,raid,rrid):
    """Read all per-raid tables in one WAL-aware transaction. No raw live DB copying."""
    rr=rr_connect(game)
    if rr is None:return False
    base=pathlib.Path(game)/'SPT_Runtime/user/mods/RaidReview/data'
    with contextlib.closing(rr):
        rr.execute('BEGIN')
        export={'schema':1,'raidId':rrid,'tables':{}}
        for table in table_names(rr):
            columns=[r[1] for r in rr.execute(f'PRAGMA table_info("{table}")')]
            if 'raidId' not in columns:continue
            rows=rr.execute(f'SELECT * FROM "{table}" WHERE raidId=?',(rrid,))
            export['tables'][table]=[dict(r) for r in rows]
        for row in export['tables'].get('comet_decision',[]):
            try:
                e=json.loads(row['detail'])
                if isinstance(e,dict):
                    # RR stores some envelope fields separately in older formats.
                    for key,rrkey in [('session','cometSession'),('sequence','sequence'),('feature','feature'),('profileId','profileId'),('name','name'),('stage','stage'),('reason','reason')]:
                        e.setdefault(key,row.get(rrkey))
                    e['_rrTimeMs']=row['time'];e['_rrRaidId']=rrid
                    insert_event(c,raid,e,'rr:'+rrid,row['id'])
            except (ValueError,TypeError):continue
        add_value(c,root,raid,'raid-review','rr:'+rrid,export)
    # Positions are separate files; retain their exact bytes and detect concurrent writes.
    for p in (base/'positions').glob(rrid+'*'):
        if p.is_file():add_file(c,root,raid,'rr-positions',p)
    return True

def context_files(c,root,game,raid,start,end):
    game=pathlib.Path(game)
    # These files can contain the whole launch. Archive as contextual evidence,
    # never pretend each line or present-day configuration belongs to an old raid.
    for p in [game/'BepInEx/LogOutput.log',game/'BepInEx/config/com.chazut.orbit.cfg']:
        if p.is_file() and p.stat().st_mtime>=start:
            with contextlib.suppress(OSError,RuntimeError):add_file(c,root,raid,'context',p,'captured-after-raid')
    for sub in ['BepInEx/COMET/performance','BepInEx/ORBIT/diagnostics']:
        for p in (game/sub).glob('*'):
            if p.is_file() and start-60<=p.stat().st_mtime<=end+180:
                add_file(c,root,raid,'performance' if 'performance' in p.name else 'orbit',p,'time-window-candidate')
    # Binary identities at collection time are labelled; historical metadata comes
    # from the recorded assembly MVID/version, never today's installed DLL.
    if time.time()-end<3600:
        versions=[]
        for p in (game/'BepInEx/plugins').rglob('*.dll'):
            versions.append({'path':str(p.relative_to(game)),'sha256':digest(p),'size':p.stat().st_size})
        add_value(c,root,raid,'environment','installed-plugin-hashes',{'captured':utc(),'files':versions},'at-collection-time')
        for folder in ['SPT_Runtime/user/mods/COMET','SPT_Runtime/user/mods/ORBIT','BepInEx/config']:
            for p in (game/folder).glob('*.json'):
                add_file(c,root,raid,'configuration',p,'at-collection-time')

def performance_report(c,root,raid):
    result=[]
    for row in c.execute("SELECT hash,original,attribution FROM artifacts WHERE raid=? AND kind='performance'",(raid,)):
        # Keep raw samples available; retain actual summaries, no invented attribution.
        summaries=[];slow=[];header={}
        with read_blob(root,row['hash']) as f:
            for line in f:
                try:e=json.loads(line)
                except (ValueError,UnicodeError):continue
                if not isinstance(e,dict):continue
                stage=str(e.get('stage',''))
                if stage in ('performance-summary','performance-end','performance-start'):summaries.append(e)
                if stage=='performance-start':header=e
                slow.extend(h for h in e.get('hitches',[]) if isinstance(h,dict))
                if len(slow)>1000:slow=sorted(slow,key=lambda h:h.get('frameMs') or 0,reverse=True)[:100]
        slow.sort(key=lambda h:h.get('frameMs') or 0,reverse=True);slow=slow[:100]
        meta=json.loads(c.execute('SELECT metadata FROM raids WHERE id=?',(raid,)).fetchone()[0])
        exact=header.get('session') and header.get('session')==meta.get('session')
        if header.get('session') and not exact:continue
        for h in slow:
            if exact and number(h.get('realtime')) is not None:
                h['nearbyEvents']=[dict(r) for r in c.execute('SELECT event_key,feature,name,reason,clock FROM events WHERE raid=? AND clock BETWEEN ? AND ? ORDER BY clock LIMIT 12',
                      (raid,h['realtime']-.25,h['realtime']+.25))]
        result.append({'source':row['original'],'attribution':'exact-session' if exact else row['attribution'],'summaries':summaries,'frames':slow})
    return result

def collect(settings,force=False):
    root=pathlib.Path(settings['archive']);game=pathlib.Path(settings['game']);root.mkdir(parents=True,exist_ok=True)
    if shutil.disk_usage(root).free < settings.get('minimumFreeBytes',1024**3):
        return {'state':'retrying','error':'Less than the configured free disk reserve; originals are untouched.'}
    groups=sessions(game);running=game_running();now=time.time();settle=settings.get('settleSeconds',30)
    eligible={s:ps for s,ps in groups.items() if now-max(p.stat().st_mtime for p in ps)>=settle and
              (tail(ps[-1]) or not running)}
    # No heavy work while a live raid has an unclosed COMET journal. Force is for
    # re-analysis of archived evidence, never for reading an actively written raid.
    newest=max(groups.values(),key=lambda ps:max(p.stat().st_mtime for p in ps),default=[])
    if running and (not newest or not tail(newest[-1])):
        return {'state':'waiting-for-raid-end','imported':0}
    raids,links=rr_index(game);rrby={r['raidId']:r for r in raids};changed=set();errors=0
    with contextlib.closing(connect(root)) as c:
        for session,paths in eligible.items():
            if active_raid(game):break
            mapped=links.get(session,[]);rrid=mapped[0] if len(mapped)==1 else None
            rid='comet-'+session
            rrmeta=rrby.get(rrid,{})
            with paths[0].open('r',encoding='utf-8-sig') as f:
                try:header=json.loads(f.readline())
                except (ValueError,UnicodeError):header={}
            if not isinstance(header,dict) or header.get('stage')!='raid-start':header={}
            closing=tail(paths[-1]) or {}
            parts=[int(re.search(r'\.part(\d+)',p.name)[1]) if '.part' in p.name else 0 for p in paths]
            expected=closing.get('segments',max(parts)+1)
            if not isinstance(expected,int) or not 1<=expected<=100000:expected=max(parts)+1
            metadata={'session':session,'rrId':rrid,'rrMapping':'exact' if rrid else 'ambiguous' if mapped else 'unavailable',
                      'map':(rrmeta.get('location') or header.get('map') or 'Unknown')+(' ('+str(rrmeta['locationVariant'])+')' if rrmeta.get('locationVariant') else ''),'time':rrmeta.get('time',header.get('utc',paths[0].name[:15])),'rr':rrmeta,
                      'missingSegments':sorted(set(range(expected))-set(parts)),'segments':len(paths)}
            old=c.execute('SELECT metadata FROM raids WHERE id=?',(rid,)).fetchone()
            oldmeta=json.loads(old[0]) if old else {}
            if not old or any(oldmeta.get(k)!=v for k,v in metadata.items()):changed.add(rid)
            # Revisit recent raids to catch RR's late summary/position flush.
            end=max(p.stat().st_mtime for p in paths)
            recent_retry=(now-end<900 and now-oldmeta.get('lastRrCapture',0)>120)
            ensure(c,rid,metadata)
            try:
                for p in paths:
                    if parse_diagnostic(c,root,rid,p):changed.add(rid)
                if rid in changed or force or recent_retry:
                    if rrid:capture_rr(c,root,game,rid,rrid)
                    end=max(p.stat().st_mtime for p in paths)
                    try:start=dt.datetime.strptime(paths[0].name[:15],'%Y%m%d-%H%M%S').replace(tzinfo=dt.timezone.utc).timestamp()
                    except ValueError:start=end-7200
                    context_files(c,root,game,rid,start,end)
                    ensure(c,rid,{'performance':performance_report(c,root,rid),'lastRrCapture':now})
                    analyze(c,rid);changed.add(rid)
                    # A preliminary RR-only entry is replaced by the same exact raid,
                    # not displayed twice. Its evidence is retained by the content store.
                    oldid='rr-'+str(rrid)
                    if rrid and c.execute('SELECT 1 FROM raids WHERE id=?',(oldid,)).fetchone():
                        for table in ('events','features','event_features','alerts','artifacts'):c.execute(f'DELETE FROM {table} WHERE raid=?',(oldid,))
                        c.execute('DELETE FROM raids WHERE id=?',(oldid,))
                    c.commit()
            except Exception:
                errors+=1;c.rollback();logging.exception('Could not archive %s; will retry',session)
        # Preserve every older RR raid, even before COMET existed. Backfill is
        # incremental and paused by the next pass when a live journal appears.
        associated={r[0] for r in c.execute("SELECT json_extract(metadata,'$.rrId') FROM raids WHERE id LIKE 'comet-%'")}
        for rr in raids:
            if rr['raidId'] in associated:continue
            if not rr.get('exitStatus'):continue
            rid='rr-'+rr['raidId'];old=c.execute('SELECT 1 FROM raids WHERE id=?',(rid,)).fetchone()
            if old and not force:continue
            # If a new raid started during historical backfill, yield immediately.
            if game_running():
                latest=max(sessions(game).values(),key=lambda ps:max(p.stat().st_mtime for p in ps),default=[])
                if latest and not tail(latest[-1]):break
            try:
                ensure(c,rid,{'rrId':rr['raidId'],'rrMapping':'exact','map':rr['location'],'time':rr['time'],'rr':rr})
                capture_rr(c,root,game,rid,rr['raidId']);analyze(c,rid);changed.add(rid);c.commit()
            except Exception:errors+=1;c.rollback();logging.exception('RR archive failed: %s',rid)
        for rid in changed:
            row=c.execute('SELECT metadata,report FROM raids WHERE id=?',(rid,)).fetchone()
            if row:
                artifacts=[dict(a) for a in c.execute('SELECT * FROM artifacts WHERE raid=?',(rid,))]
                atomic_json(root/'raids'/rid/'manifest.json',{'id':rid,'metadata':json.loads(row[0]),'artifacts':artifacts})
                atomic_json(root/'raids'/rid/'report.json',json.loads(row[1]))
    return {'state':'retrying' if errors else 'ready','imported':len(changed),'errors':errors,'updated':utc()}
