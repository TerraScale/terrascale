import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Vector overview source. The .mmd files provide a separate editable detail view.
const here = path.dirname(fileURLToPath(import.meta.url));
const esc = (s) => String(s).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
const colors = {
  rest: ['#eaf2ff', '#3778bd'], sync: ['#e4f6f1', '#17816a'],
  engine: ['#fff3df', '#b47d2b'], control: ['#f0eafb', '#7956ad'],
  neutral: ['#f0f4f8', '#73889d'],
};
for (const count of [3, 10, 20]) {
  const rows = Math.ceil(count / 5);
  const height = 1040 + rows * 108 + 126;
  const parts = [`<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="${height}" viewBox="0 0 1600 ${height}" role="img" aria-labelledby="title desc">`,
    `<title id="title">TerraScale target architecture with ${count} regions</title>`,
    `<desc id="desc">REST bootstrap through a Cloudflare Worker then direct regional REST. Reactive delivery through a Snippet and CDN. TerraBase runs SQLite, physical RF=2 and direct QUIC replication. Every member host has a local orchestrator; GRU controls mutations and three fixed global store replicas serve ${count} regions.</desc>`,
    '<defs><marker id="a" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto-start-reverse"><path d="M0 0 L8 4 L0 8 Z" fill="#536c83"/></marker></defs>',
    `<rect width="1600" height="${height}" fill="#f7f9fc"/>`,
    '<style>text{font-family:Arial,sans-serif;fill:#18334a}.body{font-size:15px}.small{font-size:15px}.h{font-size:18px;font-weight:700}.section{font-size:18px;font-weight:700;letter-spacing:.6px}</style>'];
  const text = (x,y,s,cls='body',extra='') => parts.push(`<text x="${x}" y="${y}" class="${cls}" ${extra}>${esc(s)}</text>`);
  const rect = (x,y,w,h,fill,stroke='#d2dee8',r=12) => parts.push(`<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${r}" fill="${fill}" stroke="${stroke}"/>`);
  const card = (x,y,w,h,title,lines,kind='neutral') => {
    const [fill,stroke]=colors[kind]; rect(x,y,w,h,fill,stroke);text(x+17,y+29,title,'h');
    lines.forEach((l,i)=>text(x+17,y+58+i*23,l,'body'));
  };
  const arrow = (points,label='',lx=0,ly=0,dashed=false,both=false) => {
    parts.push(`<polyline points="${points}" fill="none" stroke="#536c83" stroke-width="2" ${dashed?'stroke-dasharray="5 4"':''} marker-end="url(#a)" ${both?'marker-start="url(#a)"':''}/>`);
    if(label) {rect(lx-5,ly-17,label.length*7.8+10,23,'#f7f9fc','#f7f9fc',3);text(lx,ly,label,'small');}
  };
  rect(0,0,1600,128,'#112d43','#112d43',0);
  text(44,39,'TERRASCALE / ARCHITECTURE','section','style="fill:#78d7bb"');
  text(44,86,`${count} regions. One v1 architecture.`,'h','style="fill:#fff;font-size:34px"');
  text(1060,52,`${count*2} member hosts minimum`,'h','style="fill:#fff"');
  text(1060,82,`${count*2} local orchestrators · 3 control replicas`,'body','style="fill:#c4dbe9"');
  text(44,158,'TARGET DESIGN  ·  Blue = REST  ·  Green = reactive  ·  Amber = data/replication  ·  Purple = control','small');

  rect(32,180,1536,502,'#fff');
  text(52,212,'01  REQUESTS AND THE DATABASE','section');
  text(888,212,'OCI detail = one selected database Region, repeated as placed','small');
  card(56,264,220,116,'App / SDK',['TS · Go · Rust · .NET','Sign in / API credential','Session + idempotency']);
  card(326,264,260,116,'REST load balancer',['api.terrascale.tech','ts-proxy Worker','Placement + health + load'],'rest');
  card(646,264,290,116,'TerraBase public API',['Direct regional TLS endpoint','Auth · scope · session frontier','CRUD · read-only SQL · attach'],'engine');
  card(996,264,260,116,'SQLite + WAL',['Per database, one writer','Bytes · indexes · history','Atomic publication journal'],'engine');
  card(1316,264,228,116,'Physical follower',['Second regional host','RF=2 · durable WAL ack','Same placement'],'engine');
  arrow('276,327 326,327','REST',281,310);
  arrow('586,327 646,327','',590,310);
  arrow('936,327 996,327','',937,310);
  arrow('1256,327 1316,327','WAL',1266,310);
  arrow('166,264 166,236 791,236 791,264','Direct REST pin; failed / overloaded → select another eligible member',286,248);

  card(56,446,220,116,'Same SDK, live view',['Attach: scoped capability','Then HTTP snapshot/log','Checkpoint stays opaque'],'sync');
  card(326,446,260,116,'Snippet capability gate',['sync.api.terrascale.tech','Verify scope + generation','Remove token before cache'],'sync');
  card(646,446,290,116,'CDN + Tiered Cache',['HIT returns bytes to the SDK','MISS collapses origin fills','No Worker on this path'],'sync');
  card(996,446,260,116,'Shared result views',['Journal → incremental delta','100 ms shared publication','Encode immutable bytes once'],'sync');
  card(1316,446,228,116,'OCI Object Storage',['Native TerraBase backup · PITR','Restore to a new DB','Regional · DEK encrypted'],'engine');
  arrow('276,507 326,507','HTTP',279,490);
  arrow('586,507 646,507','gate',594,490);
  arrow('936,507 996,507','',934,490,false,true);
  arrow('1126,380 1126,446','journal',1138,420);
  arrow('1256,356 1284,356 1284,412 1430,412 1430,446','backup',1356,405);
  text(58,593,'Cache key keeps snapshot cut / log offset. Empty polls are no-store. Optional WebSocket connects directly to the member.','small');
  rect(56,612,1488,48,'#fff6e7','#e2c893',8);
  text(74,642,'CROSS-REGION: member ↔ member QUIC journal + causal CRDT merge + anti-entropy. Only Regions in that database’s immutable placement.','body');

  rect(32,706,1536,266,'#fff');
  text(52,740,'02  CONTROL AND OPERATIONS','section');
  text(834,740,'Customer documents, queries, WAL and reactive frames never enter NATS','small');
  card(56,772,250,116,'ts-deploy / Forgejo',['Check · build · sign · publish','Submit immutable candidate','No cluster rollout authority'],'control');
  card(366,772,310,116,'GRU global master',['One fenced mutation authority','Mirrors · canary · rollback','Controls regional admission'],'control');
  card(736,772,360,116,'Regional orchestrators',['Local process on EVERY member host','GRU-issued, Region-scoped authority','Durable intent and operation receipts'],'control');
  card(1156,772,388,116,'Local lifecycle on the same host',['ts-node: signed install + systemd','TBEP: health / membership / drain','TerraBase decides readiness'],'control');
  arrow('306,826 366,826','',307,809,true);
  arrow('676,826 736,826','',682,809,true);
  arrow('1096,826 1156,826','',1103,809,true);
  text(56,922,'Two hosts per Region to start. Scale from measured demand; admit/catch up before traffic. Fixed control R3: GRU + SFO + FRA.','body');
  text(56,950,'Orchestrator → DNS / KV / keys / edge promotion. estate.* → TerraBase → dashboard SDK. Grafana agents → Grafana Cloud.','small');

  const rosterY=996;
  rect(32,rosterY,1536,rows*108+62,'#fff');
  text(52,rosterY+32,`03  REGIONAL FOOTPRINT / ${count} REGIONS`,'section');
  const names=['GRU · home Region','SFO','FRA'];
  for(let i=0;i<count;i++){
    const x=56+(i%5)*299, y=rosterY+53+Math.floor(i/5)*108;
    rect(x,y,280,92,i<3?'#eee9f8':'#edf3f8',i<3?'#997cc0':'#99aec0',8);
    text(x+14,y+26,names[i]??`R${String(i+1).padStart(2,'0')} · proposed Region`,'h');
    text(x+14,y+51,'2 members + 2 local controllers','small');
    text(x+14,y+75,i===0?'Global master + 1 store replica':i<3?'1 global store replica':'Regional authority; uses global R3','small');
  }
  const fy=rosterY+rows*108+89;
  text(44,fy,'Regional services: TerraBase + ts-orchestrator + ts-node + Grafana agent on each host. Regional backups remain on OCI.','body');
  text(44,fy+27,'Estate growth does not widen database residency. A separate regional R3 needs at least 3 eligible hosts and explicit admission.','body');
  text(44,fy+54,'GRU / NATS outage pauses new control mutations; admitted data-plane service continues. Launch qualifies 3 Regions; 10/20 are design targets.','small');
  parts.push('</svg>');
  await writeFile(path.join(here,`architecture-${count}-regions.svg`),parts.join('\n')+'\n');
}
