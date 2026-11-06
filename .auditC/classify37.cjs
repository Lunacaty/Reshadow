const fs=require('fs');
const pack=JSON.parse(fs.readFileSync('data/packs/official-sv/pack.json','utf8')).cards;
const byId={};for(const c of pack)byId[c.card_id]=c;
const ids=[10111130,10113120,10114130,10131320,10132120,10132130,10133110,10133310,10163110,10232310,10253120,10324110,10362210,10363210,10371110,10373110,10374110,10542310,10543110,10554120,10562210,10573110,10674110,10723310,10761120,10773310,10774110,10811110,10831310,10833310,10834110,10844110,10903110,10932120,11034120,11044110,11053310];
const cls={A:[],B:[],C:[],D:[]};
for(const id of ids){
  const t=byId[id]?.text||'';
  if(/ctr:[XYZ]\b/.test(t)) cls.A.push(id);
  else if(/count[(]/.test(t)||/maxgroup[(]/.test(t)) cls.C.push(id);
  else if(/ctr:/.test(t)) cls.B.push(id);
  else cls.D.push(id);
}
for(const [k,v] of Object.entries(cls)) console.log(k,v.length,JSON.stringify(v));
console.log('--- A 7 张全部同构检查 ---');
for(const id of cls.A){const t=byId[id].text; console.log(id,'aura='+/aura:/.test(t),'on_spellboost='+/on_spellboost:/.test(t),'counter X by 1='+/counter X by 1/.test(t),'counter X = 0/2='+/counter X = [02]/.test(t));}
console.log('--- maxgroup 用法 ---');
for(const c of pack.filter(c=>/maxgroup[(]/.test(c.text||'')))console.log(c.card_id,c.name,'|',(c.text||'').split('\n').filter(l=>l.includes('maxgroup')).join(' ; ').trim());
console.log('--- NumOp.Cond / Arith 真实卡 ---');
for(const c of pack.filter(c=>/\(if .* then .* else .*\)/.test(c.text||'')))console.log('COND',c.card_id,c.name);
for(const c of pack.filter(c=>/count\([^)]*\) - count\(| \* | \/ |min\(|max\(/.test(c.text||'')))console.log('ARITH',c.card_id,c.name,'|',(c.text||'').split('\n').filter(l=>/ - | \* |min\(|max\(/.test(l)).slice(0,1).join('').trim());
console.log('--- 90 张中文 X 卡的 PyAST 形态 ---');
const src=JSON.parse(fs.readFileSync('sva_src.json','utf8')).cards;
const zh=Object.entries(src).filter(([id,r])=>/起始为|本卡牌的|X\+|X点|X为/.test((r.skill_texts||[]).map(x=>x.text_chs||'').join(''))).map(([id])=>Number(id));
console.log('zh hits',zh.length);
const shape={count:0,ctrX:0,ctrOther:0,prop:0,none:0};
for(const id of zh){const t=byId[id]?.text||''; if(/ctr:[XYZ]\b/.test(t))shape.ctrX++;else if(/count[(]/.test(t))shape.count++;else if(/ctr:/.test(t))shape.ctrOther++;else if(/prop:/.test(t))shape.prop++;else shape.none++;}
console.log(JSON.stringify(shape));