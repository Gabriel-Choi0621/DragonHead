using System.Globalization;
using System.Text;

namespace DragonHead.Core;

/// <summary>
/// 리포트의 '주가 기록' 대화형 차트. 봉 데이터를 JSON 으로 넣고, 그리는 건 브라우저(canvas)가 한다.
/// 1분·5분·10분·일·주·월·년 탭, 마우스를 올리면 그 봉의 시가·고가·저가·종가·거래량, 드래그로 이동, 휠로 확대.
/// 외부 스크립트 없이 파일 하나로 열린다.
/// </summary>
public static class ReportChart
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Section(IReadOnlyList<Bar> dailyHistory, IReadOnlyList<Bar> minuteHistory,
                                 IEnumerable<CatalystEvent> catalysts, IEnumerable<DateOnly> buyDays, decimal prevClose,
                                 IEnumerable<InvestorDay>? investors = null)
    {
        var tabs = new (string key, string label, ChartInterval iv, bool minute)[]
        {
            ("m1", "1분", ChartInterval.Min1, true), ("m5", "5분", ChartInterval.Min5, true), ("m10", "10분", ChartInterval.Min10, true),
            ("d", "일", ChartInterval.Day, false), ("w", "주", ChartInterval.Week, false), ("mo", "월", ChartInterval.Month, false), ("y", "년", ChartInterval.Year, false),
        };

        var json = new StringBuilder("{");
        bool firstKey = true;
        foreach (var (key, _, iv, minute) in tabs)
        {
            var src = minute ? minuteHistory : dailyHistory;
            var bars = ChartIntervals.Aggregate(src.OrderBy(b => b.Time).ToList(), iv);
            if (!firstKey) json.Append(',');
            firstKey = false;
            json.Append('"').Append(key).Append("\":[");
            for (int i = 0; i < bars.Count; i++)
            {
                var b = bars[i];
                if (i > 0) json.Append(',');
                json.Append('[').Append(b.Time.ToUnixTimeSeconds()).Append(',')
                    .Append(D(b.Open)).Append(',').Append(D(b.High)).Append(',').Append(D(b.Low)).Append(',')
                    .Append(D(b.Close)).Append(',').Append(D(b.Volume)).Append(']');
            }
            json.Append(']');
        }
        json.Append(",\"cat\":{");
        bool firstCat = true;
        // 앞으로 올 명분은 가격 기록에 붙이지 않는다 (마지막 봉에 미래 일정이 붙어 보이던 문제)
        var lastDay = dailyHistory.Count > 0 ? DateOnly.FromDateTime(dailyHistory.Max(b => b.Time).DateTime) : Kst.Today;
        if (lastDay < Kst.Today) lastDay = Kst.Today;
        foreach (var g in catalysts.Where(c => c.ParsedDate is not null && c.ParsedDate <= lastDay).GroupBy(c => c.ParsedDate!.Value))
        {
            if (!firstCat) json.Append(',');
            firstCat = false;
            json.Append('"').Append(g.Key.ToString("yyyy-MM-dd", Inv)).Append("\":").Append(Js(g.First().Title));
        }
        json.Append("},\"buy\":[").Append(string.Join(",", buyDays.Select(d => "\"" + d.ToString("yyyy-MM-dd", Inv) + "\""))).Append(']');
        json.Append(",\"base\":").Append(D(prevClose));
        // 일봉 표의 수급 열 (주식 수): 날짜 → [외국인, 기관, 개인(확정 전엔 null)]
        json.Append(",\"inv\":{").Append(string.Join(",", (investors ?? Array.Empty<InvestorDay>())
            .GroupBy(r => r.Date).Select(g => g.First())
            .Select(r => $"\"{r.Date.ToString("yyyy-MM-dd", Inv)}\":[{r.ForeignerNet},{r.InstitutionNet},{(r.IndividualNet is { } ind ? ind.ToString(Inv) : "null")}]")))
            .Append("}}");

        var sb = new StringBuilder();
        sb.Append("<h2>주가 기록 <span class='sub'>마우스를 올리면 그 봉의 가격 · 드래그로 이동 · 휠로 확대 · 더블클릭은 처음 화면</span></h2>");
        sb.Append("<div class='card' style='padding:12px'><div id='pcTabs' class='pctabs'>");
        foreach (var (key, label, _, _) in tabs)
            sb.Append($"<button data-k='{key}'>{label}</button>");
        sb.Append("<span id='pcRange' class='sub' style='margin-left:auto'></span></div>");
        sb.Append("<div id='pcWrap' style='position:relative'><canvas id='pc' style='width:100%;height:420px;display:block;cursor:crosshair'></canvas>");
        sb.Append("<div id='pcTip' class='pctip'></div></div>");
        sb.Append("<div class='pctablewrap'><table id='pcTable' class='pctable'></table></div>");
        sb.Append("<div class='sub' style='margin-top:6px'>표는 지금 차트에 보이는 봉만 최신순으로 · 줄에 마우스를 올리면 차트에 표시 · ━ <span style='color:#f0b040'>20봉 평균</span> · ━ <span style='color:#4fd1c5'>60봉 평균</span> · ┅ 기준가 · ◆ 명분 · ▲ 매수 · 분봉은 정규장(09:00~15:30)만, 앱이 받아 둔 만큼(보통 최근 1~3일) 들어 있습니다. 일·주·월·년봉 종가는 토스 일봉 기준이라 NXT 애프터마켓(~20:00) 마지막 가격이 들어가 KRX 종가와 조금 다를 수 있어요.</div></div>");
        sb.Append("<style>.pctabs{display:flex;gap:6px;align-items:center;margin-bottom:8px;flex-wrap:wrap}" +
                  ".pctabs button{background:#2c3038;color:var(--muted);border:0;border-radius:6px;padding:5px 12px;font:600 13px Pretendard,sans-serif;cursor:pointer}" +
                  ".pctabs button.on{background:var(--text);color:var(--bg)}" +
                  ".pctip{position:absolute;pointer-events:none;display:none;background:rgba(36,39,46,.96);border:1px solid #444a55;border-radius:8px;padding:8px 10px;font-size:13px;line-height:1.5;white-space:nowrap;z-index:2}" +
                  ".pctip b{font-size:13px}" +
                  ".pctablewrap{max-height:360px;overflow:auto;margin-top:10px;border-top:1px solid var(--line)}" +
                  ".pctable{font-size:13px}.pctable th{position:sticky;top:0;background:var(--card);z-index:1}" +
                  ".pctable tr.hl td{background:#2c3038}.pctable td.cat{color:var(--down);text-align:left;font-size:12px;white-space:nowrap}</style>");
        sb.Append("<script>const PC=").Append(json).Append(";\n").Append(Script).Append("</script>");
        return sb.ToString();
    }

    private static string D(decimal v) => v.ToString("0.####", Inv);

    private static string Js(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("<", "\\u003c") + "\"";

    // 그리기는 앱 차트(ChartControl)와 같은 규칙: 상승 빨강 / 하락 파랑, 20·60 이동평균, 아래 거래량.
    private const string Script = """
(function(){
const cv=document.getElementById('pc'),wrap=document.getElementById('pcWrap'),tip=document.getElementById('pcTip'),
      rng=document.getElementById('pcRange'),tabs=document.querySelectorAll('#pcTabs button');
const UP='#f05454',DN='#4c9aff',M20='#f0b040',M60='#4fd1c5',GRID='#2a2e36',MUTED='#969ca8',TEXT='#e8eaee';
const WD=['일','월','화','수','목','금','토'];
let key='d',bars=[],ma20=[],ma60=[],right=0,count=80,mouse=null,drag=null,lay=null,tableKey='',hl=-1;
const tbl=document.getElementById('pcTable');
const sgn=(v,d=0)=>(v>0?'+':'')+(d?v.toFixed(d):Math.round(v).toLocaleString('ko-KR'));
const cls=v=>v>0?'up':v<0?'down':'';
function table(first,last){
  const k=key+':'+first+':'+last;if(k===tableKey)return;tableKey=k;
  const daily=key==='d',inv=PC.inv||{},hasInv=daily&&Object.keys(inv).length>0;
  let h=`<tr><th>${minute()?'시각':'날짜'}</th><th>시가</th><th>고가</th><th>저가</th><th>종가</th><th>등락</th><th>거래량</th>`+
        (hasInv?'<th>외국인</th><th>기관</th><th>개인</th>':'')+(minute()?'':'<th style="text-align:left">명분·매수</th>')+'</tr>';
  for(let i=last;i>=first;i--){const b=bars[i],prev=i>0?bars[i-1][4]:null,ch=prev?(b[4]/prev-1)*100:null;
    const day=dayKey(b[0]),r=inv[day],mk=marks(i);
    h+=`<tr data-i='${i}'><td>${label(b[0],true)}</td><td>${fmt(b[1])}</td><td>${fmt(b[2])}</td><td>${fmt(b[3])}</td>`+
       `<td class='${cls(ch||0)}'><b>${fmt(b[4])}</b></td><td class='${cls(ch||0)}'>${ch==null?'-':sgn(ch,2)+'%'}</td><td>${fmt(b[5])}</td>`+
       (hasInv?(r?`<td class='${cls(r[0])}'>${sgn(r[0])}</td><td class='${cls(r[1])}'>${sgn(r[1])}</td><td class='${cls(r[2]||0)}'>${r[2]==null?'-':sgn(r[2])}</td>`:'<td>-</td><td>-</td><td>-</td>'):'')+
       (minute()?'':`<td class='cat'>${mk.buy?'<span style="color:#2ecc71">▲ 매수</span> ':''}${mk.cat?'◆ '+mk.cat:''}</td>`)+'</tr>';}
  tbl.innerHTML=h;
  tbl.querySelectorAll('tr[data-i]').forEach(tr=>{tr.addEventListener('mouseenter',()=>{hl=+tr.dataset.i;tr.classList.add('hl');draw();});
    tr.addEventListener('mouseleave',()=>{hl=-1;tr.classList.remove('hl');draw();});});}
const pad2=n=>String(n).padStart(2,'0');
const kst=t=>new Date((t+9*3600)*1000); // UTC 필드로 한국 시각을 읽는다
const fmt=v=>Math.round(v).toLocaleString('ko-KR');
const minute=()=>key[0]==='m'&&key!=='mo';
function label(t,short){const d=kst(t),Y=d.getUTCFullYear(),M=d.getUTCMonth()+1,D=d.getUTCDate();
  if(minute())return `${M}/${D}${short?'':' ('+WD[d.getUTCDay()]+')'} ${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}`;
  if(key==='d')return short?`${String(Y).slice(2)}.${M}.${D}`:`${Y}-${pad2(M)}-${pad2(D)} (${WD[d.getUTCDay()]})`;
  if(key==='w')return `${Y}-${pad2(M)}-${pad2(D)} 시작 주`;
  if(key==='mo')return `${Y}년 ${M}월`;return `${Y}년`;}
function dayKey(t){const d=kst(t);return `${d.getUTCFullYear()}-${pad2(d.getUTCMonth()+1)}-${pad2(d.getUTCDate())}`;}
function ma(n){const r=[];let s=0;for(let i=0;i<bars.length;i++){s+=bars[i][4];if(i>=n)s-=bars[i-n][4];r.push(i>=n-1?s/n:null);}return r;}
function marks(i){ // 일봉은 그날, 주·월·년봉은 그 기간 안
  if(minute())return {cat:null,buy:false};
  const from=dayKey(bars[i][0]),to=i+1<bars.length?dayKey(bars[i+1][0]):'9999';
  const cats=Object.keys(PC.cat).filter(d=>d>=from&&d<to&&(key!=='d'||d===from)).sort();
  const buy=PC.buy.some(d=>d>=from&&d<to&&(key!=='d'||d===from));
  return {cat:cats.length?cats.map(d=>(key==='d'?'':d.slice(5).replace('-','/')+' ')+PC.cat[d]).join(' / '):null,buy};}
function setKey(k){key=k;bars=PC[k]||[];ma20=ma(20);ma60=ma(60);
  tabs.forEach(b=>b.classList.toggle('on',b.dataset.k===k));reset();}
function reset(){const w=cv.clientWidth-90;
  count=key==='y'?Math.max(10,bars.length+2):key==='m1'?w/3:key==='mo'?w/9:w/7;
  count=Math.max(10,Math.min(count,bars.length+4));right=bars.length-1+2;clamp();draw();}
function clamp(){count=Math.max(8,Math.min(count,Math.max(20,bars.length+6)));
  const lo=Math.min(bars.length-1,count*0.4),hi=bars.length-1+Math.max(2,count*0.3);right=Math.max(Math.max(0,lo),Math.min(right,hi));}
function draw(){
  const dpr=window.devicePixelRatio||1,W=cv.clientWidth,H=cv.clientHeight;
  cv.width=W*dpr;cv.height=H*dpr;const g=cv.getContext('2d');g.setTransform(dpr,0,0,dpr,0,0);g.clearRect(0,0,W,H);
  g.font='11px Pretendard,"Malgun Gothic",sans-serif';
  if(bars.length===0){tbl.innerHTML='';tableKey='';g.fillStyle=MUTED;g.textAlign='center';g.fillText(minute()?'분봉 데이터가 없습니다 (앱에서 받아 둔 분봉이 없음)':'데이터가 없습니다',W/2,H/2);rng.textContent='';return;}
  const L=6,R=84,T=10,B=22,ph=(H-T-B)*0.76,plot={l:L,t:T,r:W-R,b:T+ph},vol={t:T+ph+10,b:H-B};
  const step=(plot.r-plot.l)/count,first=Math.max(0,Math.floor(right-count+1)),last=Math.min(bars.length-1,Math.ceil(right));
  const X=i=>plot.r-(right-i+0.5)*step;
  let hi=-Infinity,lo=Infinity,mv=1;
  for(let i=first;i<=last;i++){hi=Math.max(hi,bars[i][2]);lo=Math.min(lo,bars[i][3]);mv=Math.max(mv,bars[i][5]);
    for(const m of [ma20[i],ma60[i]])if(m!=null){hi=Math.max(hi,m);lo=Math.min(lo,m);}}
  const span=hi-lo;if(PC.base>0&&PC.base<=hi+span*0.25&&PC.base>=lo-span*0.25){hi=Math.max(hi,PC.base);lo=Math.min(lo,PC.base);}
  const p=Math.max(1,(hi-lo)*0.06);hi+=p;lo-=p;const Y=v=>plot.t+(hi-v)/(hi-lo)*(plot.b-plot.t);
  lay={plot,vol,step,X,Y,hi,lo};
  // 가격 눈금
  const raw=(hi-lo)/5,mag=Math.pow(10,Math.floor(Math.log10(raw))),nn=raw/mag,st=(nn<1.5?1:nn<3?2:nn<7?5:10)*mag;
  g.strokeStyle=GRID;g.lineWidth=1;g.fillStyle=MUTED;g.textAlign='left';
  const by=PC.base>0&&PC.base<=hi&&PC.base>=lo?Y(PC.base):-1e9;
  for(let v=Math.ceil(lo/st)*st;v<=hi;v+=st){const y=Math.round(Y(v))+.5;g.beginPath();g.moveTo(plot.l,y);g.lineTo(plot.r,y);g.stroke();if(Math.abs(y-by)>=15)g.fillText(fmt(v),plot.r+6,y+4);}
  g.save();g.beginPath();g.rect(plot.l,0,plot.r-plot.l,H);g.clip();
  // 시간 눈금
  let lastX=-1e9;
  for(let i=Math.max(1,first);i<=last;i++){const a=kst(bars[i][0]),b=kst(bars[i-1][0]);let lab=null,strong=false;
    if(minute()){if(a.getUTCDate()!==b.getUTCDate()){lab=`${a.getUTCMonth()+1}/${a.getUTCDate()}`;strong=true;}else if(a.getUTCHours()!==b.getUTCHours())lab=a.getUTCHours()+'시';}
    else if(key==='d'){if(a.getUTCFullYear()!==b.getUTCFullYear()){lab=String(a.getUTCFullYear());strong=true;}else if(a.getUTCMonth()!==b.getUTCMonth())lab=`${String(a.getUTCFullYear()).slice(2)}.${a.getUTCMonth()+1}`;}
    else if(key==='w'){if(a.getUTCFullYear()!==b.getUTCFullYear()){lab=String(a.getUTCFullYear());strong=true;}else if(a.getUTCMonth()!==b.getUTCMonth()&&a.getUTCMonth()%3===0)lab=`${String(a.getUTCFullYear()).slice(2)}.${a.getUTCMonth()+1}`;}
    else if(key==='mo'){if(a.getUTCFullYear()!==b.getUTCFullYear())lab=String(a.getUTCFullYear());}
    else lab=String(a.getUTCFullYear());
    if(!lab)continue;const x=Math.round(X(i)-step/2)+.5,w=g.measureText(lab).width;
    if(strong){g.strokeStyle='#464c58';g.beginPath();g.moveTo(x,plot.t);g.lineTo(x,vol.b);g.stroke();}
    if(x-lastX<w+10)continue;
    if(!strong){g.strokeStyle=GRID;g.beginPath();g.moveTo(x,plot.t);g.lineTo(x,vol.b);g.stroke();}
    g.fillStyle=strong?TEXT:MUTED;g.fillText(lab,x+3,vol.b+15);lastX=x;}
  // 13시 이후 음영 (분봉)
  if(minute()){g.fillStyle='rgba(255,255,255,.05)';for(let i=first;i<=last;i++)if(kst(bars[i][0]).getUTCHours()>=13)g.fillRect(X(i)-step/2,plot.t,step+.6,plot.b-plot.t);}
  // 거래량·캔들
  const body=Math.max(1,step*0.62);
  for(let i=first;i<=last;i++){const b=bars[i],up=b[4]>=b[1],h=b[5]/mv*(vol.b-vol.t);
    g.fillStyle=up?'rgba(240,84,84,.45)':'rgba(76,154,255,.45)';g.fillRect(X(i)-body/2,vol.b-h,body,Math.max(.5,h));
    g.strokeStyle=g.fillStyle=up?UP:DN;g.beginPath();g.moveTo(Math.round(X(i))+.5,Y(b[2]));g.lineTo(Math.round(X(i))+.5,Y(b[3]));g.stroke();
    if(step>=2.5){const yo=Y(b[1]),yc=Y(b[4]);g.fillRect(X(i)-body/2,Math.min(yo,yc),body,Math.max(1,Math.abs(yo-yc)));}}
  g.fillStyle=MUTED;g.fillText('거래량',plot.l+2,vol.t+11);
  for(const [m,c] of [[ma60,M60],[ma20,M20]]){g.strokeStyle=c;g.lineWidth=1.6;g.beginPath();let on=false;
    for(let i=Math.max(0,first-1);i<=Math.min(bars.length-1,last+1);i++){if(m[i]==null){on=false;continue;}const x=X(i),y=Y(m[i]);on?g.lineTo(x,y):g.moveTo(x,y);on=true;}g.stroke();}
  g.lineWidth=1;
  // 명분 ◆ · 매수 ▲
  if(!minute()&&step>=4){g.textAlign='center';g.font='bold 11px sans-serif';
    for(let i=first;i<=last;i++){const mk=marks(i);if(mk.cat){g.fillStyle=DN;g.fillText('◆',X(i),Y(bars[i][2])-5);}if(mk.buy){g.fillStyle='#2ecc71';g.fillText('▲',X(i),Y(bars[i][3])+14);}}
    g.textAlign='left';g.font='11px Pretendard,"Malgun Gothic",sans-serif';}
  if(first===0){g.fillStyle=MUTED;g.fillText('‹ 리포트에 들어 있는 가장 오래된 봉',plot.l+4,plot.t+12);}
  g.restore();
  // 기준가 점선
  if(PC.base>0&&PC.base<=hi&&PC.base>=lo){const y=Y(PC.base);g.setLineDash([5,4]);g.strokeStyle='#9aa0ac';g.beginPath();g.moveTo(plot.l,y);g.lineTo(plot.r,y);g.stroke();g.setLineDash([]);
    g.fillStyle='#9aa0ac';g.fillRect(plot.r+2,y-9,R-4,17);g.fillStyle='#111';g.font='bold 11px sans-serif';g.fillText('기준 '+fmt(PC.base),plot.r+6,y+4);g.font='11px Pretendard,sans-serif';}
  rng.textContent=`${label(bars[first][0],true)} ~ ${label(bars[last][0],true)} · ${last-first+1}봉`;
  if(!drag||!drag.moved)table(first,last);
  if(hl>=first&&hl<=last){g.fillStyle='rgba(240,176,64,.18)';g.fillRect(X(hl)-Math.max(step/2,3),plot.t,Math.max(step,6),vol.b-plot.t);}
  hover(g);}
function hover(g){
  tip.style.display='none';if(!mouse||!lay||drag&&drag.moved)return;
  const {plot,vol,step,X,Y}=lay;if(mouse.x<plot.l||mouse.x>plot.r||mouse.y<plot.t||mouse.y>vol.b)return;
  const i=Math.round(right-(plot.r-mouse.x)/step+0.5);if(i<0||i>=bars.length)return;
  const b=bars[i],prev=i>0?bars[i-1][4]:b[1],ch=prev?(b[4]/prev-1)*100:0,x=X(i);
  g.setLineDash([2,3]);g.strokeStyle='rgba(255,255,255,.35)';g.beginPath();g.moveTo(x,plot.t);g.lineTo(x,vol.b);g.stroke();
  if(mouse.y<=plot.b){g.beginPath();g.moveTo(plot.l,mouse.y);g.lineTo(plot.r,mouse.y);g.stroke();
    const pv=lay.hi-(mouse.y-plot.t)/(plot.b-plot.t)*(lay.hi-lay.lo);g.setLineDash([]);g.fillStyle='#464b55';g.fillRect(plot.r+2,mouse.y-9,78,17);g.fillStyle=TEXT;g.fillText(fmt(pv),plot.r+6,mouse.y+4);}
  g.setLineDash([]);
  const c=ch>0?'up':ch<0?'down':'',mk=marks(i);
  let h=`<b>${label(b[0])}</b><br>시가 ${fmt(b[1])} · 고가 <span class='up'>${fmt(b[2])}</span><br>저가 <span class='down'>${fmt(b[3])}</span> · 종가 <b class='${c}'>${fmt(b[4])}</b> <span class='${c}'>(${ch>0?'+':''}${ch.toFixed(2)}%)</span><br>거래량 ${fmt(b[5])}`;
  if(ma20[i]!=null)h+=`<br><span style='color:${M20}'>20봉 ${fmt(ma20[i])}</span> · <span style='color:${M60}'>60봉 ${ma60[i]!=null?fmt(ma60[i]):'-'}</span>`;
  if(minute()&&PC.base>0)h+=`<br>기준가 대비 ${((b[4]/PC.base-1)*100).toFixed(2)}%`;
  if(mk.buy)h+=`<br><span style='color:#2ecc71'>▲ 매수</span>`;if(mk.cat)h+=`<br><span style='color:${DN}'>◆ ${mk.cat}</span>`;
  tip.innerHTML=h;tip.style.display='block';
  const tw=tip.offsetWidth,th=tip.offsetHeight;
  tip.style.left=(x+14+tw>plot.r?x-tw-14:x+14)+'px';tip.style.top=Math.max(0,Math.min(mouse.y-th/2,cv.clientHeight-th))+'px';}
cv.addEventListener('mousemove',e=>{const r=cv.getBoundingClientRect();mouse={x:e.clientX-r.left,y:e.clientY-r.top};
  if(drag&&lay){const dx=mouse.x-drag.x;if(Math.abs(dx)>3)drag.moved=true;if(drag.moved){right=drag.right-dx/lay.step;clamp();cv.style.cursor='grabbing';}}draw();});
cv.addEventListener('mouseleave',()=>{mouse=null;drag=null;cv.style.cursor='crosshair';draw();});
cv.addEventListener('mousedown',e=>{if(e.button===0)drag={x:mouse?mouse.x:0,right,moved:false};});
window.addEventListener('mouseup',()=>{if(drag){drag=null;cv.style.cursor='crosshair';draw();}});
cv.addEventListener('dblclick',reset);
cv.addEventListener('wheel',e=>{if(!lay)return;e.preventDefault();const f=e.deltaY<0?1/1.25:1.25,x=Math.max(lay.plot.l,Math.min(mouse?mouse.x:lay.plot.r,lay.plot.r));
  const fr=(lay.plot.r-x)/(lay.plot.r-lay.plot.l),anchor=right-fr*count;count*=f;clamp();right=anchor+fr*count;clamp();draw();},{passive:false});
tabs.forEach(b=>b.addEventListener('click',()=>setKey(b.dataset.k)));
window.addEventListener('resize',draw);
setKey('d');
})();
""";
}
