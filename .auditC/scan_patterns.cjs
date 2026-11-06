const fs=require('fs');
const pack=JSON.parse(fs.readFileSync('data/packs/official-sv/pack.json','utf8')).cards;
const pats={
 'propPlayerStat': /prop:(HandCount|DeckCount|BoardCount|CemeteryCount|BanishedCount|PlayPoint|MaxEvolvePoint|TurnIndex|DamageDealtThisTurn|CardsPlayedThisTurn|CardsPlayedThisMatch)/,
 'neq': /!=/,
 'countExpr': /count[(]/,
 'ctrAbility': /ctr:(cards_destroyed_by_ability|cards_banished_by_ability|cards_returned_to_hand|cards_discarded_by_ability|fuse_kinds|crest|clues|earth_sigil)/,
 'ctrXYZ': /ctr:[XYZ]/,
 'maxgroup': /maxgroup[(]/,
 'historyEntered': /history=EnteredThisMatch/,
 'isEntered': /is EnteredThisTurn|is Destroyed/,
};
for(const [k,re] of Object.entries(pats)){
  const hits=pack.filter(c=>re.test(c.text||'')).map(c=>c.card_id);
  console.log(k, hits.length, JSON.stringify(hits.slice(0,30)));
}
const dump=(name,re)=>{console.log('--- '+name+' ---');for(const c of pack){const ln=(c.text||'').split('\n').filter(s=>re.test(s)); if(ln.length) console.log(c.card_id,c.name,'|',ln.slice(0,3).join(' ; ').trim());}};
dump('NEQ',pats.neq);
dump('propPlayerStat',pats.propPlayerStat);
dump('isEntered/Destroyed',pats.isEntered);