#!/usr/bin/env python3
import argparse, collections, hashlib, json, zipfile
from pathlib import Path

CHILD={'node','device','subdevice','group','tag'}

def read_mpp(path):
    with zipfile.ZipFile(path) as z:
        text=z.read('File1').decode('cp1251')
    pairs=json.loads(text, object_pairs_hook=lambda p:p)
    def conv(obj,parent=None):
        n={'props':{},'children':[],'parent':parent}
        for k,v in obj:
            if isinstance(v,list):
                if k.lower() in CHILD: n['children'].append(conv(v,n))
            else: n['props'][k]='' if v is None else str(v)
        return n
    return conv(pairs)

def walk(n):
    yield n
    for c in n['children']: yield from walk(c)

def model(n):
    return n['props'].get('NameDevice') or n['props'].get('DeviceID') or ''

def relmap(device):
    out={}
    def rec(n,prefix=''):
        for c in n['children']:
            name=c['props'].get('NameInTree','')
            rel=name if not prefix else prefix+'.'+name
            cat=c['props'].get('Category','')
            out[(cat,rel)] = (c['props'].get('Type',''),c['props'].get('Access',''))
            rec(c,rel)
    rec(device)
    return out

def json_relmap(t):
    out={}
    def rec(n,prefix=''):
        for c in n.get('Children',[]):
            rel=c.get('Name','') if not prefix else prefix+'.'+c.get('Name','')
            kind=int(c.get('Kind',6)); cat={2:'Device',3:'SubDevice',4:'Group',5:'Teg'}.get(kind,'Unknown')
            p=c.get('Properties',{})
            out[(cat,rel)] = (p.get('Type',''),p.get('Access',''))
            rec(c,rel)
    rec(t)
    return out

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('mpp')
    ap.add_argument('--catalog',default=str(Path(__file__).resolve().parents[1]/'templates'/'device_templates.json'))
    a=ap.parse_args()
    root=read_mpp(a.mpp)
    catalog=json.loads(Path(a.catalog).read_text(encoding='utf-8'))
    by={x['ModelId']:x for x in catalog['Models']}
    errors=[]
    counts=collections.Counter()
    for d in (n for n in walk(root) if n['props'].get('Category')=='Device'):
        m=model(d); counts[m]+=1
        if m not in by:
            errors.append(f'{m}: no catalog template'); continue
        src=relmap(d); dst=json_relmap(by[m]['Template'])
        missing=set(src)-set(dst)
        if missing: errors.append(f'{m}: missing {len(missing)} paths, first={sorted(missing)[:5]}')
        for k,v in src.items():
            if k in dst and k[0]=='Teg' and v != dst[k]:
                errors.append(f'{m}: type/access mismatch {k[1]} source={v} template={dst[k]}')
                break
    print('MPP SHA256:',hashlib.sha256(Path(a.mpp).read_bytes()).hexdigest())
    print('instances:',dict(counts))
    for m,e in by.items():
        rm=json_relmap(e['Template'])
        print(f"{m}: template paths={len(rm)}, tags={sum(1 for k in rm if k[0]=='Teg')}, source={e['SourcePath']}")
    if errors:
        print('FAIL')
        for e in errors[:50]: print('  ',e)
        raise SystemExit(1)
    print('PASS: every path/type/access used by every device instance in MPP is reproduced by its embedded model template.')

if __name__=='__main__': main()
