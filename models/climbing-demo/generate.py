# Makes the CSV files of the demo climbing logbook. Everything is invented (people, routes, dates), and a fixed seed makes the
# output the same every time. Run: python generate.py <folder to write to>
import csv, random, datetime as dt, re, sys
out = sys.argv[1]
R = random.Random(20240611)

areas = [
 ("gb","Great Britain","country",""),("fr","France","country",""),("es","Spain","country",""),("it","Italy","country",""),
 ("us","United States","country",""),("gr","Greece","country",""),
 ("peak","Peak District","region","gb"),("lakes","Lake District","region","gb"),("wales","Snowdonia","region","gb"),
 ("south","South West England","region","gb"),("scot","Scottish Highlands","region","gb"),("yorks","Yorkshire Dales","region","gb"),
 ("ceuse","Ceuse","region","fr"),("font","Fontainebleau","region","fr"),("cham","Chamonix","region","fr"),
 ("cat","Catalonia","region","es"),("and","Andalucia","region","es"),("mall","Mallorca","region","es"),
 ("dol","Dolomites","region","it"),("fin","Finale Ligure","region","it"),
 ("ut","Utah","region","us"),("ca","California","region","us"),("kal","Kalymnos","region","gr"),
 ("stan","Stanage","area","peak"),("burb","Burbage","area","peak"),("froggatt","Froggatt","area","peak"),
 ("gogarth","Gogarth","area","wales"),("llanb","Llanberis Pass","area","wales"),
 ("gimmer","Langdale","area","lakes"),("borrow","Borrowdale","area","lakes"),
 ("portland","Portland","area","south"),("swanage","Swanage","area","south"),("cheddar","Cheddar Gorge","area","south"),
 ("cairn","Cairngorms","area","scot"),("skye","Isle of Skye","area","scot"),
 ("malham","Malham","area","yorks"),("kilnsey","Kilnsey","area","yorks"),
 ("siurana","Siurana","area","cat"),("margalef","Margalef","area","cat"),
 ("chulilla","Chulilla","area","and"),("sapo","El Chorro","area","and"),
 ("moab","Moab","area","ut"),("josh","Joshua Tree","area","ca"),
 ("bleau3","Bas Cuvier","area","font"),("mont","Mont Aigu","area","font"),
]

crags = [
 ("stanage-pop","Stanage Popular End","stan","Gritstone",53.349,-1.631),
 ("stanage-high","Stanage High Neb","stan","Gritstone",53.358,-1.636),
 ("burbage-n","Burbage North","burb","Gritstone",53.337,-1.617),
 ("burbage-s","Burbage South","burb","Gritstone",53.329,-1.612),
 ("froggatt-e","Froggatt Edge","froggatt","Gritstone",53.307,-1.627),
 ("curbar","Curbar Edge","froggatt","Gritstone",53.296,-1.632),
 ("gogarth-main","Main Cliff","gogarth","Sea cliff quartzite",53.309,-4.690),
 ("gogarth-north","North Stack Wall","gogarth","Sea cliff quartzite",53.317,-4.693),
 ("dinas-cromlech","Dinas Cromlech","llanb","Rhyolite",53.094,-4.036),
 ("clogwyn","Clogwyn y Grochan","llanb","Rhyolite",53.107,-4.108),
 ("gimmer-crag","Gimmer Crag","gimmer","Rhyolite",54.449,-3.101),
 ("raven","Raven Crag","gimmer","Rhyolite",54.436,-3.075),
 ("shepherds","Borrowdale Crag","borrow","Volcanic",54.557,-3.151),
 ("portland-cave","The Cuttings","portland","Limestone",50.541,-2.440),
 ("portland-bill","Blacknor","portland","Limestone",50.532,-2.457),
 ("dancing","Dancing Ledge","swanage","Limestone",50.594,-2.003),
 ("cheddar-main","Sunset Wall","cheddar","Limestone",51.281,-2.763),
 ("coire","Coire an t-Sneachda","cairn","Granite",57.119,-3.649),
 ("skye-cuillin","Sgurr nan Gillean","skye","Gabbro",57.249,-6.198),
 ("malham-cove","Malham Cove","malham","Limestone",54.073,-2.157),
 ("gordale","Gordale Scar","malham","Limestone",54.076,-2.139),
 ("kilnsey-main","Kilnsey Main Overhang","kilnsey","Limestone",54.121,-2.011),
 ("ceuse-face","Face Nord","ceuse","Limestone",44.510,5.940),
 ("ceuse-berlin","Berlin","ceuse","Limestone",44.508,5.944),
 ("cuvier","Cuvier Rempart","bleau3","Sandstone",48.374,2.627),
 ("apremont","Apremont","mont","Sandstone",48.395,2.570),
 ("aiguilles","Aiguille du Midi South Face","cham","Granite",45.879,6.887),
 ("siurana-can","Can Piqui","siurana","Limestone",41.254,0.935),
 ("siurana-pil","El Pati","siurana","Limestone",41.256,0.933),
 ("margalef-lab","Laboratori","margalef","Conglomerate",41.313,0.745),
 ("chulilla-pared","Pared de los Cuchillos","chulilla","Limestone",39.654,-0.893),
 ("chorro-main","Escalera Arabe","sapo","Limestone",36.918,-4.756),
 ("kal-grande","Grande Grotta","kal","Limestone",36.978,26.936),
 ("kal-sikati","Sikati Cave","kal","Limestone",36.996,26.935),
 ("dol-tre","Tre Cime di Lavaredo","dol","Dolomite",46.619,12.302),
 ("fin-monte","Monte Cucco","fin","Limestone",44.171,8.331),
 ("moab-wall","Wall Street","moab","Sandstone",38.598,-109.626),
 ("josh-hidden","Hidden Valley","josh","Granite",34.012,-116.168),
 ("mall-sa","Sa Gubia","mall","Limestone",39.704,2.752),
]
crag_area={c[0]:c[2] for c in crags}
amap={a[0]:a for a in areas}

UKT=("uk_trad","trad",["D","VD","HVD","S","HS","MVS 4b","HVS 5a","HVS 5b","E1 5b","E1 5c","E2 5b","E2 5c","E3 5c","E3 6a","E4 6a"])
FR=("french","sport",["5+","6a","6a+","6b","6b+","6c","6c+","7a","7a+","7b","7b+","7c"])
FONT=("font","boulder",["4","5","5+","6a","6a+","6b","6b+","6c","7a"])
VG=("v","boulder",["V0","V1","V2","V3","V4","V5","V6"])
YDS=("yds","trad",["5.6","5.7","5.8","5.9","5.10a","5.10b","5.10c","5.11a"])
ALP=("alpine","alpine",["PD","AD","D","TD"])
def country(c):
    a=amap[crag_area[c]][3]
    return amap[a][3]
def kind(c):
    cn=country(c)
    if cn=="gb": return UKT
    if c in("cuvier","apremont"): return FONT
    if c=="aiguilles": return ALP
    if cn=="us": return YDS if c=="josh-hidden" else VG
    return FR
adj=["Silent","Hollow","Crimson","Wandering","Slender","Broken","Iron","Gentle","Northern","Dancing","Hidden","Burning","Pale","Rusty","Lonely","Golden","Sleepy","Wild","Narrow","Amber","Shattered","Quiet","Bitter","Velvet","Crooked"]
noun=["Arete","Groove","Corner","Crack","Slab","Wall","Rib","Chimney","Buttress","Flake","Traverse","Roof","Pillar","Direct","Route","Edge","Gully","Face","Ramp","Nose"]
used=set(); routes=[]
def rname():
    while True:
        n=f"{R.choice(adj)} {R.choice(noun)}"
        if n not in used: used.add(n); return n
for c in crags:
    sysn,disc,gr=kind(c[0])
    for i in range(R.randint(9,14)):
        g=R.choice(gr)
        length={"trad":R.randint(15,45),"sport":R.randint(15,35),"boulder":R.randint(3,6),"alpine":R.randint(200,600)}[disc]
        pitches=1 if disc!="alpine" else R.randint(5,12)
        if disc=="trad" and R.random()<0.12: pitches=R.randint(2,4); length=pitches*R.randint(30,45)
        routes.append(dict(id=f"r{len(routes)+1}",name=rname(),crag=c[0],grade=g,system=sysn,discipline=disc,length=length,pitches=pitches))

def rank(system,g):
    if system=="uk_trad":
        adjl=["M","MOD","D","HD","VD","HVD","MS","S","HS","MVS","VS","HVS"]
        p=g.split(); a=p[0].upper()
        r=len(adjl)+int(a[1:]) if a.startswith("E") else adjl.index(a)
        if len(p)>1:
            m=re.match(r"(\d)([abc])",p[1]); r+=(int(m[1])*3+ord(m[2])-97)/100
        return r
    if system in("french","font"):
        m=re.match(r"f?(\d+)([ABCabc])?(\+)?",g)
        return int(m[1])*10+((ord(m[2].lower())-97) if m[2] else 0)*2+(0.5 if m[3] else 0)
    if system=="v":
        return int(re.match(r"V(\d+)",g)[1])
    if system=="yds":
        m=re.match(r"5\.(\d+)([abcd]|\+|-)?",g); s=m[2] or ""
        off={"":1.5,"-":.5,"+":2.5}.get(s,(ord(s)-97) if s else 0)
        return int(m[1])*10+off
    if system=="alpine": return ["F","PD","AD","D","TD","ED"].index(g)
for r in routes: r["rank"]=rank(r["system"],r["grade"])

partners=["Alex Marlow","Bryn Tavish","Cass Eldridge","Dana Wyke","Eli Ferrant","Fenn Holloway","Gwen Ashdown","Hal Corvin","Isla Penhale","Jo Kestrel","Kit Brannock","Lena Vosper","Mo Redfern","Nell Garrick","Otis Lorne","Pia Thorne","Quin Abbott","Remy Calloway","Sasha Fell","Tobi Wren"]

FOREIGN={"fr":["ceuse-face","ceuse-berlin","cuvier","apremont","aiguilles"],"es":["siurana-can","siurana-pil","margalef-lab","chulilla-pared","chorro-main","mall-sa"],
 "gr":["kal-grande","kal-sikati"],"it":["dol-tre","fin-monte"],"us":["moab-wall","josh-hidden"]}
ftrips=[("fr",2016,7),("es",2016,11),("gr",2017,10),("es",2018,2),("fr",2018,6),("it",2018,9),("es",2019,3),("fr",2019,8),("us",2019,10),
 ("es",2021,3),("gr",2021,10),("fr",2022,5),("es",2022,11),("it",2023,6),("us",2023,10),("es",2024,3),("fr",2024,7),("gr",2024,10),("es",2025,2),("fr",2025,6)]
home_regions=[("peak",["stanage-pop","stanage-high","burbage-n","burbage-s","froggatt-e","curbar"]),("wales",["gogarth-main","gogarth-north","dinas-cromlech","clogwyn"]),
 ("lakes",["gimmer-crag","raven","shepherds"]),("south",["portland-cave","portland-bill","dancing","cheddar-main"]),("yorks",["malham-cove","gordale","kilnsey-main"]),("scot",["coire","skye-cuillin"])]
trips=[]
for y in range(2016,2026):
    n=R.randint(14,20) if y!=2020 else 6
    for _ in range(n):
        reg,cs=R.choice(home_regions) if R.random()>0.45 else home_regions[0]
        m=R.choice([3,4,5,5,6,6,7,8,9,9,10,11]) if y!=2020 else R.choice([7,8,9,10])
        s=dt.date(y,m,R.randint(1,27)); nights=R.choice([0,0,0,1,1,2,2,3])
        trips.append(dict(start=s,end=s+dt.timedelta(nights),country="gb",crags=R.sample(cs,min(len(cs),R.randint(1,2))),abroad=False))
for c,y,m in ftrips:
    s=dt.date(y,m,R.randint(1,20)); nights=R.randint(5,12)
    trips.append(dict(start=s,end=s+dt.timedelta(nights),country=c,crags=R.sample(FOREIGN[c],min(len(FOREIGN[c]),R.randint(1,3))),abroad=True))
trips.sort(key=lambda t:t["start"])
seen={}
for i,t in enumerate(trips,1):
    t["id"]=f"t{i}"
    nm=f"{t['start']:%b %Y} - {amap[t['country']][1]}"
    seen[nm]=seen.get(nm,0)+1
    t["name"]=nm if seen[nm]==1 else f"{nm} #{seen[nm]}"
    t["key"]=f"{t['start']:%Y}-{i}"

by_crag={}
for r in routes: by_crag.setdefault(r["crag"],[]).append(r)
def style_for(r):
    d=r["discipline"]
    if d=="trad": return R.choices([("lead","Led"),("onsight","Onsight"),("second","Seconded"),("redpoint","Redpoint"),("flash","Flash"),("top_rope","Top rope")],[40,20,18,10,6,6])[0]
    if d=="sport": return R.choices([("redpoint","Redpoint"),("onsight","Onsight"),("flash","Flash"),("hangdog","Hangdog"),("lead","Led")],[35,20,10,15,20])[0]
    if d=="boulder": return R.choices([("send","Send"),("flash","Flash"),("onsight","Onsight")],[70,15,15])[0]
    return R.choices([("lead","Led"),("second","Seconded")],[45,55])[0]
notes=["Greasy start","Great climbing","Hard for the grade","Soft for the grade","Cold fingers","Windy belay","Perfect conditions","Bring extra cams","Polished holds","Long run-out","Loved it","Crowded","Rain at the top","Would repeat"]
ascents=[]; at=[]; n=0
for t in trips:
    days=(t["end"]-t["start"]).days+1
    per_day=R.randint(2,5) if t["abroad"] else R.randint(1,4)
    for di in range(days):
        day=t["start"]+dt.timedelta(di)
        pool=by_crag[R.choice(t["crags"])]
        pat=R.choice(partners[:12]) if R.random()<.8 else ""
        for r in R.sample(pool,min(len(pool),per_day)):
            n+=1; st,raw=style_for(r)
            prec="day"; dd=day.isoformat()
            if t["start"].year in(2016,2017) and R.random()<.12: prec="month"; dd=day.replace(day=1).isoformat()
            ascents.append(dict(id=f"a{n}",name=r["name"],date=dd,prec=prec,style=st,raw=raw,notes=R.choice(notes) if R.random()<.3 else "",
              rating=R.choice([1,2,3,3,4,4,5]) if R.random()<.5 else "",source=R.choice(["manual","ukc","ukc","csv"]),route=r["id"],partner=pat if R.random()<.9 else ""))
            at.append((f"a{n}",t["id"],"true" if prec!="day" else "false"))
pid={p:f"pt{i+1}" for i,p in enumerate(partners)}
def w(name,header,rows):
    with open(f"{out}/{name}","w",newline="",encoding="utf-8") as f:
        c=csv.writer(f,lineterminator="\n"); c.writerow(header); c.writerows(rows)
w("areas.csv",["id","name","kind","parent"],areas)
w("crags.csv",["id","name","area","rock_type","lat","lon","aliases","notes"],[(c[0],c[1],c[2],c[3],c[4],c[5],"","") for c in crags])
w("routes.csv",["id","name","crag","grade","grade_system","discipline","length_m","pitches","grade_rank"],
  [(r["id"],r["name"],r["crag"],r["grade"],r["system"],r["discipline"],r["length"],r["pitches"],r["rank"]) for r in routes])
w("partners.csv",["id","name"],[(pid[p],p) for p in partners])
w("trips.csv",["id","name","trip_key","start_date","end_date","sort_date","country","abroad","home","notes","approximate","dates_partly_unknown","manual"],
  [(t["id"],t["name"],t["key"],t["start"],t["end"],t["start"],t["country"],str(t["abroad"]).lower(),str(not t["abroad"]).lower(),"","false","false","false") for t in trips])
w("ascents.csv",["id","name","date","date_precision","style","style_raw","notes","my_rating","source","route","partner"],
  [(a["id"],a["name"],a["date"],a["prec"],a["style"],a["raw"],a["notes"],a["rating"],a["source"],a["route"],pid.get(a["partner"],"")) for a in ascents])
w("ascent_trips.csv",["ascent","trip","inferred"],at)
print(len(areas),len(crags),len(routes),len(trips),len(ascents),len(partners))
