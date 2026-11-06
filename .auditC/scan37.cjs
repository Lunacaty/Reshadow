const fs=require('fs');
const src=JSON.parse(fs.readFileSync('sva_src.json','utf8')).cards;
const pack=JSON.parse(fs.readFileSync('data/packs/official-sv/pack.json','utf8')).cards;
const byId={}; for(const c of pack) byId[c.card_id]=c;
const isHit=(s)=>/starts at|Increase X|X is the number of/i.test(s||'');
const ids=[];
for(const [id,rec] of Object.entries(src)){
  const t=(rec.skill_texts||[]).map(x=>x.text_eng||'').join(' ');
  if(isHit(t)) ids.push(Number(id));
}
ids.sort((a,b)=>a-b);
console.log('HITS',ids.length,JSON.stringify(ids));
for(const id of ids){
  const rec=src[String(id)], c=byId[String(id)]||byId[id];
  const en=(rec.skill_texts||[]).map(x=>x.text_eng||'').join(' / ').replace(/<[^>]*>/g,'');
  const zh=(rec.skill_texts||[]).map(x=>x.text_chs||'').join(' / ').replace(/<[^>]*>/g,'');
  const py=c?(c.text||''):'NO_PACK';
  const body=py.split('\n').slice(1).map(s=>s.trim()).filter(Boolean);
  console.log('@@@'+id+'|'+(c?c.name:'?')+'|'+en.slice(0,300)+'|'+zh.slice(0,300)+'|'+body.join(' ; ').slice(0,700));
}
