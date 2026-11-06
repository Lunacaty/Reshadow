const fs=require('fs');
const src=JSON.parse(fs.readFileSync('sva_src.json','utf8')).cards;
for(const id of [10331110,10332110,10333110,10562210,10262310,10162110,10264120,10874110,10111130,10643310]){
  const s=src[String(id)];
  console.log(id+' | '+(s?.skill_texts||[]).map(x=>x.text_chs).join(' / ').replace(/<[^>]*>/g,''));
}