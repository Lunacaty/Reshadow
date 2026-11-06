const fs=require('fs');
const src=JSON.parse(fs.readFileSync('sva_src.json','utf8')).cards;
const pack=JSON.parse(fs.readFileSync('data/packs/official-sv/pack.json','utf8')).cards;
const byId={}; for(const c of pack) byId[c.card_id]=c;
const pats=['起始为','本卡牌的','X+','Y+','Z+','X起始','的 X','的X'];
const counts={}; const hits={};
for(const p of pats){counts[p]=0;hits[p]=[];}
for(const [id,rec] of Object.entries(src)){
  const txts=(rec.skill_texts||[]).map(x=>x.text_chs||'').join('\n');
  for(const p of pats) if(txts.includes(p)){counts[p]++; hits[p].push(id);}
}
console.log('COUNTS',JSON.stringify(counts));
console.log('UNION', new Set(Object.values(hits).flat()).size);
const union=[...new Set(Object.values(hits).flat())].map(Number).sort((a,b)=>a-b);
console.log('UNION_IDS',JSON.stringify(union));
for(const id of union){
  const rec=src[String(id)]; const c=byId[id];
  const t=(rec.skill_texts||[]).map(x=>x.text_chs||'').join(' / ').replace(/<[^>]*>/g,'');
  console.log('---',id, c?c.name:'NO_PACK', '|', t.slice(0,260));
  if(c) console.log((c.text||'').split('\n').slice(1).join('\n    '));
}
