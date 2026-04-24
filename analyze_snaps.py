import csv, math, sys

path = r'C:\Users\paul_\AppData\Roaming\AcEvoFfbTuner\snapshots\snapshot_20260421_005843_324.txt'

rows = []
with open(path, 'r') as f:
    for i, line in enumerate(f, 1):
        if i >= 80:
            parts = line.strip().split(',')
            if len(parts) >= 14:
                try:
                    r = {
                        'idx': i - 80,
                        'Time': parts[0],
                        'SpeedKmh': float(parts[1]),
                        'SteerAngle': float(parts[2]),
                        'ForceOut': float(parts[3]),
                        'RawFF': float(parts[4]),
                        'Compress': float(parts[5]),
                        'LUT': float(parts[6]),
                        'Slip': float(parts[7]),
                        'Damping': float(parts[8]),
                        'Dynamic': float(parts[9]),
                        'MzFront': float(parts[10]),
                        'FxFront': float(parts[11]),
                        'FyFront': float(parts[12]),
                        'Clipping': parts[13],
                    }
                    rows.append(r)
                except:
                    pass

print(f'Loaded {len(rows)} rows')

for i in range(1, len(rows)):
    rows[i]['dForceOut'] = abs(rows[i]['ForceOut'] - rows[i-1]['ForceOut'])
    rows[i]['dSteerAngle'] = abs(rows[i]['SteerAngle'] - rows[i-1]['SteerAngle'])
    rows[i]['dRawFF'] = abs(rows[i]['RawFF'] - rows[i-1]['RawFF'])
    rows[i]['dMzFront'] = abs(rows[i]['MzFront'] - rows[i-1]['MzFront'])
    rows[i]['signForceOut'] = 1 if rows[i]['ForceOut'] > 0 else (-1 if rows[i]['ForceOut'] < 0 else 0)
    rows[i]['signSteerAngle'] = 1 if rows[i]['SteerAngle'] > 0 else (-1 if rows[i]['SteerAngle'] < 0 else 0)

rows[0]['dForceOut'] = 0
rows[0]['dSteerAngle'] = 0
rows[0]['dRawFF'] = 0
rows[0]['dMzFront'] = 0
rows[0]['signForceOut'] = 1 if rows[0]['ForceOut'] > 0 else (-1 if rows[0]['ForceOut'] < 0 else 0)
rows[0]['signSteerAngle'] = 1 if rows[0]['SteerAngle'] > 0 else (-1 if rows[0]['SteerAngle'] < 0 else 0)

THRESHOLD = 0.08
snaps = [r for r in rows if r['dForceOut'] > THRESHOLD]

print(f'\n{"="*60}')
print(f'SNAP EVENTS (|ForceOut delta| > {THRESHOLD})')
print(f'{"="*60}')
print(f'Total snaps: {len(snaps)}')

cats = {'0-60': 0, '60-120': 0, '120-200': 0, '200+': 0}
for s in snaps:
    sp = s['SpeedKmh']
    if sp < 60: cats['0-60'] += 1
    elif sp < 120: cats['60-120'] += 1
    elif sp < 200: cats['120-200'] += 1
    else: cats['200+'] += 1
print(f'By speed range (km/h): {cats}')

print(f'\n--- ALL SNAP EVENTS DETAIL ---')
for s in snaps:
    idx = s['idx']
    prev = rows[idx - 1] if idx > 0 else rows[0]
    print(f"ROW {idx:>4} | T={s['Time']} | Speed={s['SpeedKmh']:.1f} km/h")
    print(f"  SteerAngle: {prev['SteerAngle']:+.4f} -> {s['SteerAngle']:+.4f}  dSteer={s['dSteerAngle']:.4f}")
    print(f"  ForceOut:   {prev['ForceOut']:+.6f} -> {s['ForceOut']:+.6f}  dForce={s['dForceOut']:.6f}")
    print(f"  RawFF:      {prev['RawFF']:+.6f} -> {s['RawFF']:+.6f}  dRawFF={s['dRawFF']:.6f}")
    print(f"  MzFront:    {prev['MzFront']:+.6f} -> {s['MzFront']:+.6f}  dMz={s['dMzFront']:.6f}")
    print(f"  Damping:    {s['Damping']:.6f}   Slip: {s['Slip']:.6f}")
    print()

# --- RAPID SUCCESSION ---
print(f'{"="*60}')
print(f'RAPID SUCCESSION / OSCILLATION CLUSTERS (within 10 ticks)')
print(f'{"="*60}')
snap_indices = [s['idx'] for s in snaps]
clusters = []
current_cluster = []
for si in snap_indices:
    if not current_cluster or si - current_cluster[-1] <= 10:
        current_cluster.append(si)
    else:
        if len(current_cluster) >= 2:
            clusters.append(list(current_cluster))
        current_cluster = [si]
if len(current_cluster) >= 2:
    clusters.append(list(current_cluster))

if clusters:
    print(f'Found {len(clusters)} oscillation cluster(s):')
    for ci, c in enumerate(clusters):
        t_start = rows[c[0]]['Time']
        t_end = rows[c[-1]]['Time']
        forces = [rows[i]['ForceOut'] for i in c]
        print(f'  Cluster {ci+1}: rows {c[0]}-{c[-1]} ({len(c)} snaps, T={t_start} to {t_end})')
        print(f'    ForceOut values: {[round(f,4) for f in forces]}')
        sign_changes = sum(1 for j in range(1, len(forces)) if (forces[j] > 0) != (forces[j-1] > 0))
        print(f'    Sign reversals within cluster: {sign_changes}/{len(forces)-1}')
        for ri in c:
            r = rows[ri]
            rp = rows[ri-1]
            print(f'      Row {ri}: ForceOut {rp["ForceOut"]:+.4f} -> {r["ForceOut"]:+.4f}  Steer {rp["SteerAngle"]:+.4f} -> {r["SteerAngle"]:+.4f}  Mz {rp["MzFront"]:+.4f} -> {r["MzFront"]:+.4f}')
else:
    print('No rapid-succession clusters found (all snaps isolated).')

# --- PATTERN ANALYSIS ---
print(f'\n{"="*60}')
print(f'PATTERN ANALYSIS')
print(f'{"="*60}')

# 1. Steer direction reversals at snap points
reversals = 0
for s in snaps:
    idx = s['idx']
    if idx >= 2:
        prev = rows[idx-1]
        pprev = rows[idx-2]
        steer_reversed = (prev['SteerAngle'] - pprev['SteerAngle']) * (s['SteerAngle'] - prev['SteerAngle']) < 0
        if steer_reversed:
            reversals += 1
print(f'Snaps coinciding with steer direction reversal: {reversals}/{len(snaps)} ({100*reversals/max(1,len(snaps)):.1f}%)')

# 2. Mz spikes at snap points
mz_at_snaps = [s['dMzFront'] for s in snaps]
avg_mz_snap = sum(mz_at_snaps)/max(1,len(mz_at_snaps))
avg_mz_all = sum(r['dMzFront'] for r in rows[1:])/max(1,len(rows)-1)
print(f'Avg |dMzFront| at snap points: {avg_mz_snap:.6f}')
print(f'Avg |dMzFront| overall:        {avg_mz_all:.6f}')
print(f'Ratio (snap/overall):          {avg_mz_snap/max(0.000001,avg_mz_all):.1f}x')

# 3. Slip spikes at snap points
slip_at_snaps = [s['Slip'] for s in snaps]
avg_slip_snap = sum(slip_at_snaps)/max(1,len(slip_at_snaps))
avg_slip_all = sum(r['Slip'] for r in rows)/max(1,len(rows))
print(f'Avg Slip at snap points: {avg_slip_snap:.6f}')
print(f'Avg Slip overall:        {avg_slip_all:.6f}')
print(f'Ratio (snap/overall):    {avg_slip_snap/max(0.000001,avg_slip_all):.1f}x')

# 4. Damping at snap points
damp_at_snaps = [s['Damping'] for s in snaps]
avg_damp_snap = sum(damp_at_snaps)/max(1,len(damp_at_snaps))
avg_damp_all = sum(r['Damping'] for r in rows)/max(1,len(rows))
print(f'Avg Damping at snap points: {avg_damp_snap:.6f}')
print(f'Avg Damping overall:        {avg_damp_all:.6f}')
print(f'Ratio (snap/overall):       {avg_damp_snap/max(0.000001,avg_damp_all):.1f}x')

# 5. Steer delta correlation
dsteer_snaps = [s['dSteerAngle'] for s in snaps]
avg_dsteer_snap = sum(dsteer_snaps)/max(1,len(dsteer_snaps))
avg_dsteer_all = sum(r['dSteerAngle'] for r in rows[1:])/max(1,len(rows)-1)
print(f'Avg |dSteerAngle| at snap points: {avg_dsteer_snap:.6f}')
print(f'Avg |dSteerAngle| overall:        {avg_dsteer_all:.6f}')
print(f'Ratio (snap/overall):             {avg_dsteer_snap/max(0.000001,avg_dsteer_all):.1f}x')

# --- FORCE vs STEER OPPOSITION ---
print(f'\n{"="*60}')
print(f'FORCE vs STEER DIRECTION (CENTER-SNAP CHECK)')
print(f'{"="*60}')
opposed = 0
nonzero_both = 0
for r in rows:
    sf = 1 if r['ForceOut'] > 0 else (-1 if r['ForceOut'] < 0 else 0)
    ss = 1 if r['SteerAngle'] > 0 else (-1 if r['SteerAngle'] < 0 else 0)
    if sf != 0 and ss != 0:
        nonzero_both += 1
        if sf != ss:
            opposed += 1
pct_opposed = 100 * opposed / max(1, nonzero_both)
print(f'Rows with both ForceOut and SteerAngle nonzero: {nonzero_both}/{len(rows)}')
print(f'Rows where ForceOut OPPOSES SteerAngle: {opposed} ({pct_opposed:.1f}%)')

opposed_snaps = 0
nonzero_snaps = 0
for s in snaps:
    sf = 1 if s['ForceOut'] > 0 else (-1 if s['ForceOut'] < 0 else 0)
    ss = 1 if s['SteerAngle'] > 0 else (-1 if s['SteerAngle'] < 0 else 0)
    if sf != 0 and ss != 0:
        nonzero_snaps += 1
        if sf != ss:
            opposed_snaps += 1
print(f'At snap points - nonzero: {nonzero_snaps}, opposed: {opposed_snaps} ({100*opposed_snaps/max(1,nonzero_snaps):.1f}%)')

# Check near-center (|SteerAngle| < 0.05)
center_rows = [r for r in rows if abs(r['SteerAngle']) < 0.05 and r['ForceOut'] != 0]
center_opposed = sum(1 for r in center_rows if r['ForceOut'] != 0 and ((r['ForceOut'] > 0) != (r['SteerAngle'] > 0)))
print(f'Near-center (|SteerAngle|<0.05) with nonzero force: {len(center_rows)}')
print(f'Near-center with opposing force: {center_opposed} ({100*center_opposed/max(1,len(center_rows)):.1f}%)')

# --- OVERALL STATISTICS ---
print(f'\n{"="*60}')
print(f'OVERALL STATISTICS')
print(f'{"="*60}')
deltas = [r['dForceOut'] for r in rows[1:]]
fo_vals = [r['ForceOut'] for r in rows]
mean_dfo = sum(deltas)/len(deltas)
max_dfo = max(deltas)
mean_fo = sum(fo_vals)/len(fo_vals)
std_fo = math.sqrt(sum((f - mean_fo)**2 for f in fo_vals)/len(fo_vals))
high_force = sum(1 for f in fo_vals if abs(f) > 0.3)
pct_high = 100 * high_force / len(fo_vals)

# Median delta
sorted_deltas = sorted(deltas)
median_dfo = sorted_deltas[len(sorted_deltas)//2]
# 95th percentile
p95_dfo = sorted_deltas[int(len(sorted_deltas)*0.95)]
# 99th percentile
p99_dfo = sorted_deltas[int(len(sorted_deltas)*0.99)]

print(f'Mean |ForceOut delta|:    {mean_dfo:.6f}')
print(f'Median |ForceOut delta|:  {median_dfo:.6f}')
print(f'95th pctile |delta|:      {p95_dfo:.6f}')
print(f'99th pctile |delta|:      {p99_dfo:.6f}')
print(f'Max  |ForceOut delta|:    {max_dfo:.6f}')
print(f'Std dev ForceOut:         {std_fo:.6f}')
print(f'Mean ForceOut:            {mean_fo:.6f}')
print(f'Range ForceOut:           [{min(fo_vals):.6f}, {max(fo_vals):.6f}]')
print(f'|ForceOut| > 0.3:         {high_force}/{len(fo_vals)} ({pct_high:.1f}%)')

print(f'\nMaxSlewRate from profile:  0.350')
exceed_slew = sum(1 for d in deltas if d > 0.35)
print(f'Deltas exceeding MaxSlewRate (0.35): {exceed_slew}')

# Additional: top 20 largest deltas
print(f'\n--- TOP 20 LARGEST |ForceOut delta| ---')
top20 = sorted(range(1,len(rows)), key=lambda i: rows[i]['dForceOut'], reverse=True)[:20]
for rank, i in enumerate(top20):
    r = rows[i]
    rp = rows[i-1]
    print(f"  {rank+1:>2}. Row {i:>4} T={r['Time']} | dF={r['dForceOut']:.6f} | {rp['ForceOut']:+.4f}->{r['ForceOut']:+.4f} | Steer {rp['SteerAngle']:+.4f}->{r['SteerAngle']:+.4f} | Speed={r['SpeedKmh']:.0f}")

# Distribution of delta magnitudes
print(f'\n--- DELTA DISTRIBUTION ---')
bins = [0, 0.01, 0.02, 0.04, 0.06, 0.08, 0.10, 0.15, 0.20, 0.30, 0.50, 1.0]
for b in range(len(bins)-1):
    count = sum(1 for d in deltas if bins[b] <= d < bins[b+1])
    pct = 100*count/len(deltas)
    bar = '#' * int(pct)
    print(f'  [{bins[b]:.2f}, {bins[b+1]:.2f}): {count:>5} ({pct:>5.1f}%) {bar}')

print('\nAnalysis complete.')
