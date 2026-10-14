# Converts the routes of the workshop map "Cache prefire by someBOT"
# (https://steamcommunity.com/sharedfiles/filedetails/?id=3770489042), used with the author's
# permission, into OpenPrefirePrac profiles for de_cache.
#
#   1. steamcmd +login anonymous +workshop_download_item 730 3770489042 +quit
#   2. extract scripts/vscripts/cache_prefire.vjs_c from the downloaded VPK and run
#      `strings -n 3 cache_prefire.vjs_c > script.txt`
#   3. python3 convert_somebot_cache.py script.txt ../maps/de_cache
#
# Positions are origin (feet) space on de_cache, the same as OpenPrefirePrac profiles. Peek slots
# are left out (they need a trigger mechanic OpenPrefirePrac doesn't have).
import re, sys, os
src, outdir = sys.argv[1], sys.argv[2]
t = open(src, encoding='utf-8', errors='replace').read()
start = t.index('const ROUTES$1')
NUM = r'(-?[\d.]+)'
VEC = r'\{ x: ' + NUM + r', y: ' + NUM + r', z: ' + NUM + r' \}'
ANG = r'\{ pitch: ' + NUM + r', yaw: ' + NUM + r', roll: ' + NUM + r' \}'
SLOT = re.compile(r'\{ id: "([a-z_0-9]+)", order: (\d+), pos: ' + VEC + r',\s*angles: ' + ANG +
                  r', callout: "([^"]*)",(?:\s*[A-Za-z]+: [^,{}]+,)*?\s*tier: "([a-z]+)", showFrom: ' + NUM)
ROUTES = {  # source key -> (OPP file name, bots at once, random ratio)
    'mid': ('mid_to_heaven', 8, 0.55),
    'a':   ('a_main', 8, 0.45),
    'b':   ('b_main', 8, 0.45),
}
names = [v[0] for v in ROUTES.values()]
def section(seg, key):
    m = re.search(r'\n        ' + key + r': \[(.*?)\n        \],', seg, re.S)
    return m.group(1) if m else ''
def f(x): return ('%.6f' % float(x)).rstrip('0').rstrip('.')
os.makedirs(outdir, exist_ok=True)
for key, (fname, n, ratio) in ROUTES.items():
    m = re.search(r'\n    ' + key + r': \{\n        id: "' + key + '"', t[start:])
    seg = t[start + m.start():]
    nxt = re.search(r'\n    [a-z_]+: \{\n        id: "', seg[10:])
    seg = seg[:nxt.start() + 10] if nxt else seg
    st = re.search(r'start: \{.*?pos: ' + VEC + r',\s*angles: ' + ANG, seg, re.S)
    slots = []
    # Peek slots only appear once the player reaches a trigger; OpenPrefirePrac has no such mechanic,
    # so only the regular bot slots are converted.
    for kind in ('botSlots',):
        for s in SLOT.findall(section(seg, kind)):
            slots.append(dict(id=s[0], order=int(s[1]), pos=s[2:5], ang=s[5:8], callout=s[8], tier=s[9],
                              show=float(s[10]), kind=kind))
    # Route progression: showFrom is the fraction along the route where a slot becomes relevant.
    slots.sort(key=lambda s: (s['show'], s['kind'] != 'botSlots', s['order']))
    ways = re.findall(VEC, section(seg, 'waypoints'))
    lines = [' '.join(x for x in names if x != fname), f'{n} {ratio}',
             ' '.join(f(v) for v in st.groups())]
    for s in slots:
        lines.append(' '.join(f(v) for v in (*s['pos'], *s['ang'])) + f" False # {s['callout']} ({s['id']}, someBOT)")
    for w in ways:  # guiding-line joints sit 9 units above the floor (eye - 55)
        lines.append(f"{f(w[0])} {f(w[1])} {f(float(w[2]) + 9)}")
    open(os.path.join(outdir, fname + '.txt'), 'w').write('\n'.join(lines) + '\n')
    print(f"{fname}: {len(slots)} bots ({sum(s['kind']=='peekSlots' for s in slots)} peek), {len(ways)} guide points, start {st.groups()[:3]}")
