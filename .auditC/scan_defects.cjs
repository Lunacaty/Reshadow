const fs=require('fs');
const pack=JSON.parse(fs.readFileSync('data/packs/official-sv/pack.json','utf8')).cards;
const src=JSON.parse(fs.readFileSync('sva_src.json','utf8')).cards;
const groups={
  'count(card:#id+History)': /count[(]card:#\d+@[^)]*history=EnteredThisMatch/,
  'count(#id w/o History)': /count[(]card:#\d+@[^)]*All[)]/ ,
  'where has ward without @': /where has ward/,
  'is Evolved/CostModified': /is CostModified|has Fuse|has Last Words|has OnSpellboost/,
  'leader +leader': /opponent:board \+leader All|self:board \+leader All/,
  'Neq != ': /!=/,
  'distinct': /distinct All|distinct\)/,
  'Random(count': /Random[(]count/,
};
for(const [k,re] of Object.entries(groups)){const h=pack.filter(c=>re.test(c.text||'')).map(c=>c.card_id);console.log(k,h.length,JSON.stringify(h.slice(0,25)));}
console.log('--- real neq card + official text ---');
for(const c of pack.filter(c=>/!=/.test(c.text||''))){const s=src[String(c.card_id)];console.log(c.card_id,c.name,'|',(s?.skill_texts||[]).map(x=>x.text_chs).join(' / ').replace(/<[^>]*>/g,''));}
console.log('--- real count(card:#id + history) cards ---');
for(const c of pack.filter(c=>/count[(]card:#\d+@[^)]*history=EnteredThisMatch/.test(c.text||''))){const s=src[String(c.card_id)];console.log(c.card_id,c.name,'|',(s?.skill_texts||[]).map(x=>x.text_chs).join(' / ').replace(/<[^>]*>/g,'').slice(0,200),'|',(c.text||'').split('\n').filter(l=>l.includes('count(')).slice(0,2).join(' ; '));}