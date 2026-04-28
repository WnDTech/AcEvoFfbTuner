using System.Text;

namespace AcEvoFfbTuner.Services;

public static class ReplayVisualizerService
{
    public static string GenerateHtml(
        string csvData,
        string profileName,
        float torqueNm,
        string? profilerStats = null)
    {
        var csvLines = csvData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataRows = new List<string>();
        foreach (var line in csvLines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Time,") || string.IsNullOrEmpty(trimmed)) continue;
            dataRows.Add(trimmed);
        }

        string dataJson = BuildDataJson(dataRows);
        string statsHtml = string.IsNullOrEmpty(profilerStats)
            ? ""
            : $"<pre id=\"stats\">{EscapeHtml(profilerStats)}</pre>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>FFB Session Replay — {EscapeHtml(profileName)}</title>
<style>
*{{margin:0;padding:0;box-sizing:border-box}}
body{{background:#0d1117;color:#c9d1d9;font-family:'Segoe UI',system-ui,sans-serif;overflow-x:hidden}}
.container{{max-width:1400px;margin:0 auto;padding:16px}}
h1{{font-size:18px;color:#58a6ff;margin-bottom:4px}}
.subtitle{{font-size:12px;color:#8b949e;margin-bottom:12px}}
.top-row{{display:flex;gap:16px;margin-bottom:12px;flex-wrap:wrap}}
.wheel-panel{{background:#161b22;border-radius:8px;padding:16px;flex:0 0 280px;text-align:center}}
.gauges-panel{{background:#161b22;border-radius:8px;padding:16px;flex:1;min-width:300px}}
.gauge-grid{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px}}
.gauge{{text-align:center;padding:4px}}
.gauge-label{{font-size:10px;color:#8b949e;text-transform:uppercase;letter-spacing:1px}}
.gauge-value{{font-size:22px;font-weight:700;font-variant-numeric:tabular-nums}}
.chart-section{{background:#161b22;border-radius:8px;padding:16px;margin-bottom:12px}}
.chart-header{{font-size:13px;color:#8b949e;margin-bottom:6px}}
canvas{{width:100%;display:block;border-radius:4px}}
.controls{{background:#161b22;border-radius:8px;padding:12px;margin-bottom:12px;display:flex;align-items:center;gap:12px;flex-wrap:wrap}}
.btn{{background:#21262d;border:1px solid #30363d;color:#c9d1d9;padding:6px 16px;border-radius:6px;cursor:pointer;font-size:13px}}
.btn:hover{{background:#30363d}}
.btn.active{{background:#238636;border-color:#2ea043;color:#fff}}
input[type=range]{{flex:1;min-width:200px;accent-color:#58a6ff}}
.speed-label{{font-size:12px;color:#8b949e;min-width:80px;text-align:right;font-variant-numeric:tabular-nums}}
.legend{{display:flex;gap:16px;margin-top:6px;flex-wrap:wrap}}
.legend-item{{display:flex;align-items:center;gap:4px;font-size:11px;color:#8b949e}}
.legend-dot{{width:10px;height:3px;border-radius:1px}}
#stats{{background:#161b22;border-radius:8px;padding:16px;font-size:11px;overflow-x:auto;white-space:pre-wrap;max-height:300px;overflow-y:auto}}
.torque-bar{{width:100%;height:8px;background:#21262d;border-radius:4px;margin-top:4px;position:relative}}
.torque-fill{{height:100%;border-radius:4px;transition:width 0.05s}}
.pedal-bar{{height:20px;background:#21262d;border-radius:4px;position:relative;overflow:hidden}}
.pedal-fill{{height:100%;border-radius:4px;transition:width 0.05s}}
</style>
</head>
<body>
<div class=""container"">
<h1>FFB Session Replay</h1>
<div class=""subtitle"">{EscapeHtml(profileName)} — Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} — Wheel: {torqueNm:F1}Nm</div>

<div class=""top-row"">
<div class=""wheel-panel"">
<canvas id=""wheelCanvas"" width=""248"" height=""248""></canvas>
<div style=""margin-top:8px"">
<div class=""gauge-label"">Steering Angle</div>
<div id=""steerText"" class=""gauge-value"" style=""color:#ffd600"">0.0°</div>
</div>
</div>
<div class=""gauges-panel"">
<div class=""gauge-grid"">
<div class=""gauge"">
<div class=""gauge-label"">Speed</div>
<div id=""speedText"" class=""gauge-value"" style=""color:#58a6ff"">0</div>
<div class=""gauge-label"">km/h</div>
</div>
<div class=""gauge"">
<div class=""gauge-label"">Force Out</div>
<div id=""forceText"" class=""gauge-value"" style=""color:#ff4500"">0.000</div>
<div class=""torque-bar""><div id=""torqueFill"" class=""torque-fill"" style=""width:0;background:#ff4500""></div></div>
</div>
<div class=""gauge"">
<div class=""gauge-label"">Raw FF</div>
<div id=""rawText"" class=""gauge-value"" style=""color:#00e676"">0.000</div>
</div>
<div class=""gauge"">
<div class=""gauge-label"">Mz Front</div>
<div id=""mzText"" class=""gauge-value"" style=""color:#ffab00"">0.000</div>
</div>
<div class=""gauge"">
<div class=""gauge-label"">Fx Front</div>
<div id=""fxText"" class=""gauge-value"" style=""color:#00bcd4"">0.000</div>
</div>
<div class=""gauge"">
<div class=""gauge-label"">Fy Front</div>
<div id=""fyText"" class=""gauge-value"" style=""color:#ce93d8"">0.000</div>
</div>
</div>
<div style=""display:flex;gap:16px;margin-top:12px"">
<div style=""flex:1"">
<div class=""gauge-label"">Throttle</div>
<div class=""pedal-bar""><div id=""gasFill"" class=""pedal-fill"" style=""width:0;background:#4caf50""></div></div>
</div>
<div style=""flex:1"">
<div class=""gauge-label"">Brake</div>
<div class=""pedal-bar""><div id=""brakeFill"" class=""pedal-fill"" style=""width:0;background:#e53935""></div></div>
</div>
</div>
</div>
</div>

<div class=""controls"">
<button class=""btn"" id=""playBtn"" onclick=""togglePlay()"">▶ Play</button>
<button class=""btn"" onclick=""stepBack()"">◀◀</button>
<button class=""btn"" onclick=""stepForward()"">▶▶</button>
<input type=""range"" id=""scrubber"" min=""0"" max=""0"" value=""0"" oninput=""scrubTo(this.value)"">
<span class=""speed-label"" id=""timeLabel"">0:00.0 / 0:00.0</span>
<span class=""speed-label"" style=""min-width:40px"">Speed:</span>
<button class=""btn"" onclick=""setSpeed(0.5)"">0.5x</button>
<button class=""btn active"" onclick=""setSpeed(1)"">1x</button>
<button class=""btn"" onclick=""setSpeed(2)"">2x</button>
<button class=""btn"" onclick=""setSpeed(4)"">4x</button>
</div>

<div class=""chart-section"">
<div class=""chart-header"">Force &amp; Steering Over Time</div>
<canvas id=""mainChart"" height=""200""></canvas>
<div class=""legend"">
<div class=""legend-item""><div class=""legend-dot"" style=""background:#ff4500""></div>Force Out</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:#00e676""></div>Raw FF</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:#ffd600""></div>Steer Angle</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:#00bcd4""></div>Compress</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:#ff5722""></div>Slip</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:#9c27b0""></div>Damping</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:#795548""></div>Dynamic</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:rgba(76,175,80,0.5)""></div>Gas</div>
<div class=""legend-item""><div class=""legend-dot"" style=""background:rgba(229,57,53,0.5)""></div>Brake</div>
</div>
</div>

<div class=""chart-section"">
<div class=""chart-header"">Speed (km/h)</div>
<canvas id=""speedChart"" height=""80""></canvas>
</div>

{statsHtml}
</div>

<script>
const D={dataJson};
const COLS={{T:0,SPD:1,STR:2,FO:3,RFF:4,CMP:5,LUT:6,SLP:7,DMP:8,DYN:9,MZ:10,FX:11,FY:12,CLP:13,GAS:14,BRK:15}};
const N=D.length;
let idx=0,playing=false,speed=1,animId=null,lastTime=0;
const TORQUE_NM={torqueNm.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)};

const wheelCanvas=document.getElementById('wheelCanvas');
const wheelCtx=wheelCanvas.getContext('2d');
const mainCanvas=document.getElementById('mainChart');
const mainCtx=mainCanvas.getContext('2d');
const speedCanvas=document.getElementById('speedChart');
const speedCtx=speedCanvas.getContext('2d');

function parseTime(t){{
let p=t.split(':');return parseInt(p[0])*60+parseFloat(p[1]);
}}
let t0=N>0?parseTime(D[0][COLS.T]):0;
let tEnd=N>0?parseTime(D[N-1][COLS.T]):0;
let duration=tEnd-t0;
document.getElementById('scrubber').max=N-1;

function drawWheel(angle){{
const w=wheelCanvas.width,h=wheelCanvas.height,cx=w/2,cy=h/2,r=90;
wheelCtx.clearRect(0,0,w,h);
wheelCtx.save();
wheelCtx.translate(cx,cy);
wheelCtx.rotate(angle*Math.PI*2);
const rim=wheelCtx.createRadialGradient(0,0,r-18,0,0,r);
rim.addColorStop(0,'#2d333b');rim.addColorStop(1,'#444c56');
wheelCtx.beginPath();wheelCtx.arc(0,0,r,0,Math.PI*2);
wheelCtx.lineWidth=18;wheelCtx.strokeStyle=rim;wheelCtx.stroke();
wheelCtx.lineWidth=2;wheelCtx.strokeStyle='#58a6ff';wheelCtx.beginPath();wheelCtx.arc(0,0,r+1,0,Math.PI*2);wheelCtx.stroke();
wheelCtx.beginPath();wheelCtx.arc(0,0,r-10,0,Math.PI*2);wheelCtx.lineWidth=1;wheelCtx.strokeStyle='#30363d';wheelCtx.stroke();
for(let i=0;i<3;i++){{
let a=i*Math.PI*2/3;
wheelCtx.beginPath();wheelCtx.moveTo(Math.cos(a)*(r-18),Math.sin(a)*(r-18));
wheelCtx.lineTo(Math.cos(a)*25,Math.sin(a)*25);
wheelCtx.lineWidth=6;wheelCtx.strokeStyle='#30363d';wheelCtx.stroke();
}}
wheelCtx.beginPath();wheelCtx.arc(0,0,18,0,Math.PI*2);
wheelCtx.fillStyle='#21262d';wheelCtx.fill();
wheelCtx.lineWidth=2;wheelCtx.strokeStyle='#30363d';wheelCtx.stroke();
wheelCtx.beginPath();wheelCtx.arc(0,0,5,0,Math.PI*2);
wheelCtx.fillStyle='#ff4500';wheelCtx.fill();
wheelCtx.restore();
wheelCtx.beginPath();wheelCtx.moveTo(cx,cy-r-12);wheelCtx.lineTo(cx-5,cy-r-6);wheelCtx.lineTo(cx+5,cy-r-6);wheelCtx.closePath();
wheelCtx.fillStyle='#ff4500';wheelCtx.fill();
}}

function drawChart(canvas,ctx,cols,colors,fillCols){{
const w=canvas.width=canvas.offsetWidth*2;
const h=canvas.height=canvas.offsetHeight*2;
ctx.clearRect(0,0,w,h);
if(N<2)return;
const pad={{l:60,r:20,t:10,b:20}};
const cw=w-pad.l-pad.r,ch=h-pad.t-pad.b;
ctx.strokeStyle='#21262d';ctx.lineWidth=1;
ctx.beginPath();ctx.moveTo(pad.l,pad.t);ctx.lineTo(pad.l,pad.t+ch);ctx.lineTo(pad.l+cw,pad.t+ch);ctx.stroke();
const viewStart=Math.max(0,idx-600);
const viewEnd=Math.min(N-1,idx+200);
const viewN=viewEnd-viewStart+1;
function gx(i){{return pad.l+(i-viewStart)/(viewN-1)*cw;}}
function gy(v,mn,mx){{return pad.t+ch-((v-mn)/(mx-mn||1))*ch;}}
cols.forEach((col,ci)=>{{
let mn=Infinity,mx=-Infinity;
for(let i=viewStart;i<=viewEnd;i++){{let v=parseFloat(D[i][col]);if(v<mn)mn=v;if(v>mx)mx=v;}}
let range=mx-mn;if(range<0.01)range=0.01;mn-=range*0.05;mx+=range*0.05;
ctx.beginPath();ctx.strokeStyle=colors[ci];ctx.lineWidth=2;
for(let i=viewStart;i<=viewEnd;i++){{let x=gx(i),y=gy(parseFloat(D[i][col]),mn,mx);i===viewStart?ctx.moveTo(x,y):ctx.lineTo(x,y);}}
ctx.stroke();
if(fillCols&&fillCols[ci]){{
ctx.lineTo(gx(viewEnd),pad.t+ch);ctx.lineTo(gx(viewStart),pad.t+ch);ctx.closePath();
ctx.fillStyle=fillCols[ci];ctx.fill();
}}
}});
if(idx>=viewStart&&idx<=viewEnd){{
let x=gx(idx);
ctx.beginPath();ctx.moveTo(x,pad.t);ctx.lineTo(x,pad.t+ch);
ctx.strokeStyle='rgba(88,166,255,0.5)';ctx.lineWidth=2;ctx.stroke();
}}
}}
}}

function update(){{
if(N===0)return;
const row=D[idx];
const spd=parseFloat(row[COLS.SPD]);
const str=parseFloat(row[COLS.STR]);
const fo=parseFloat(row[COLS.FO]);
const rff=parseFloat(row[COLS.RFF]);
const mz=parseFloat(row[COLS.MZ]);
const fx=parseFloat(row[COLS.FX]);
const fy=parseFloat(row[COLS.FY]);
const gas=parseFloat(row[COLS.GAS]);
const brk=parseFloat(row[COLS.BRK]);

drawWheel(str);
document.getElementById('steerText').textContent=(str*450).toFixed(1)+'°';
document.getElementById('speedText').textContent=spd.toFixed(0);
document.getElementById('forceText').textContent=fo.toFixed(3);
document.getElementById('rawText').textContent=rff.toFixed(3);
document.getElementById('mzText').textContent=mz.toFixed(3);
document.getElementById('fxText').textContent=fx.toFixed(3);
document.getElementById('fyText').textContent=fy.toFixed(3);
document.getElementById('torqueFill').style.width=Math.min(Math.abs(fo)*100,100)+'%';
document.getElementById('gasFill').style.width=(gas*100)+'%';
document.getElementById('brakeFill').style.width=(brk*100)+'%';

drawChart(mainCanvas,mainCtx,
[COLS.FO,COLS.RFF,COLS.STR,COLS.CMP,COLS.SLP,COLS.DMP,COLS.DYN,COLS.GAS,COLS.BRK],
['#ff4500','#00e676','#ffd600','#00bcd4','#ff5722','#9c27b0','#795548','rgba(76,175,80,0.5)','rgba(229,57,53,0.5)'],
[null,null,null,null,null,null,'rgba(125,85,72,0.1)','rgba(76,175,80,0.08)','rgba(229,57,53,0.08)']);

drawChart(speedCanvas,speedCtx,
[COLS.SPD],['#58a6ff'],['rgba(88,166,255,0.1)']);

let curT=parseTime(row[COLS.T])-t0;
document.getElementById('timeLabel').textContent=fmtTime(curT)+' / '+fmtTime(duration);
document.getElementById('scrubber').value=idx;
}}

function fmtTime(s){{let m=Math.floor(s/60);let sec=(s%60).toFixed(1);if(sec.length<4)sec='0'+sec;return m+':'+sec;}}

function togglePlay(){{
playing=!playing;
const btn=document.getElementById('playBtn');
btn.textContent=playing?'⏸ Pause':'▶ Play';
btn.classList.toggle('active',playing);
if(playing){{lastTime=performance.now();animate();}}
else if(animId){{cancelAnimationFrame(animId);animId=null;}}
}}

function animate(){{
if(!playing)return;
let now=performance.now();
let dt=(now-lastTime)/1000*speed;
lastTime=now;
let curTime=parseTime(D[idx][COLS.T]);
let targetTime=curTime+dt;
while(idx<N-1&&parseTime(D[idx+1][COLS.T])<=targetTime)idx++;
if(idx>=N-1){{idx=N-1;playing=false;document.getElementById('playBtn').textContent='▶ Play';document.getElementById('playBtn').classList.remove('active');update();return;}}
update();
animId=requestAnimationFrame(animate);
}}

function stepBack(){{idx=Math.max(0,idx-30);update();}}
function stepForward(){{idx=Math.min(N-1,idx+30);update();}}
function scrubTo(v){{idx=parseInt(v);update();}}
function setSpeed(s){{speed=s;document.querySelectorAll('.controls .btn').forEach(b=>{{if(b.textContent.includes('x'))b.classList.remove('active');}});event.target.classList.add('active');}}

update();
window.addEventListener('resize',()=>update());
</script>
</body>
</html>";
    }

    private static string BuildDataJson(List<string> rows)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var fields = rows[i].Split(',');
            sb.Append('[');
            for (int j = 0; j < fields.Length; j++)
            {
                if (j > 0) sb.Append(',');
                var f = fields[j].Trim();
                if (f.Equals("True", StringComparison.OrdinalIgnoreCase))
                    sb.Append("true");
                else if (f.Equals("False", StringComparison.OrdinalIgnoreCase))
                    sb.Append("false");
                else if (float.TryParse(f, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out _))
                    sb.Append(f);
                else
                    sb.Append('"').Append(EscapeJs(f)).Append('"');
            }
            sb.Append(']');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeJs(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "\\'");
}
