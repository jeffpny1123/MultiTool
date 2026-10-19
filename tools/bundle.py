import sys,struct,os,zlib,json
SIG=b'\x8b\x12\x02\xb9\x6a\x61\x20\x38\x72\x7b\x93\x02\x14\xd7\xa0\x32\x13\xf5\xb9\xe6\xef\xae\x33\x18\xee\x3b\x2d\xce\x24\xb3\x6a\xae'
def rstr(d,o):
    l=0;s=0
    while True:
        b=d[o];o+=1;l|=(b&0x7f)<<s;s+=7
        if b<0x80:break
    return d[o:o+l].decode(),o+l
def parse(d):
    i=d.find(SIG); hoff=struct.unpack_from('<q',d,i-8)[0]; o=hoff
    major,minor,n=struct.unpack_from('<IIi',d,o); o+=12
    bid,o=rstr(d,o)
    depsOff,depsSize,rcOff,rcSize,flags=struct.unpack_from('<qqqqQ',d,o); o+=40
    ents=[]
    for _ in range(n):
        off,size,csize=struct.unpack_from('<qqq',d,o);o+=24
        t=d[o];o+=1
        p,o=rstr(d,o); ents.append(dict(off=off,size=size,csize=csize,type=t,path=p))
    return dict(sigpos=i,hoff=hoff,major=major,minor=minor,bid=bid,deps=(depsOff,depsSize),rc=(rcOff,rcSize),flags=flags,ents=ents)
if __name__=='__main__':
    d=open(sys.argv[1],'rb').read(); m=parse(d); out=sys.argv[2]
    for e in m['ents']:
        data=d[e['off']:e['off']+(e['csize'] or e['size'])]
        if e['csize']: data=zlib.decompress(data)
        fp=os.path.join(out,e['path']); os.makedirs(os.path.dirname(fp) or out,exist_ok=True); open(fp,'wb').write(data)
    m2=dict(m); json.dump(m2,open(os.path.join(out,'_manifest.json'),'w'),indent=1)
    print(m['major'],m['minor'],m['bid'],len(m['ents']),hex(m['flags']), min(e['off'] for e in m['ents']))
