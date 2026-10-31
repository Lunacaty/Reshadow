# -*- coding: utf-8 -*-
"""生成测试卡池。

设计约束（全部来自对官方卡池 cards_eng_flat.json 的原句抽取）：
  1. 用语必须与官方一致，不自造写法。
     例：官方只有 "to your hand" / "to hand"，**没有** "to its hand"。
  2. 能力头固定用 <b><color=Keyword>Fanfare</color></b>: 形式。
  3. 卡名引用用 <b><i><color=Keyword>Name</color></i></b> 形式。
  4. 关键词（Ward / Storm ...）裸写为 <b><color=Keyword>Ward</color></b>。
  5. 条件前缀用 " - "（Combo / Necromancy / Earth Rite），
     能力头用 ":"（Fanfare / Evolve / Super-Evolve / Last Words / Engage / Enhance）。
"""
import io, json, os

def H(name):   # 能力头 / 关键词
    return '<b><color=Keyword>%s</color></b>' % name

def N(name):   # 卡名
    return '<b><i><color=Keyword>%s</color></i></b>' % name

cards = []
def add(name, kind, cost, atk, life, text, trait='', cls='neutral'):
    cards.append({
        'card_id': 81000000 + len(cards),
        'name_eng': name,
        'type': kind,          # 1=Follower 2=Amulet 3=Spell 4=Crest
        'cost': cost,
        'atk': atk,
        'life': life,
        'class': cls,
        'tribe_name_eng': trait,
        'eng_texts': [text],
        'eng_text': text,
    })

# ==================== 触发器（全部用官方原文形态）====================
add('Wayfarer Apprentice', 1, 1, 1, 1, H('Fanfare') + ': Draw a card.')
add('Bladeborn Adept', 1, 2, 2, 2, H('Evolve') + ': Deal 2 damage to the enemy leader.')
add('Marrowstone Colossus', 1, 3, 3, 3, H('Super-Evolve') + ': Restore 3 defense to your leader.')
add('Dying Ember Sprite', 1, 4, 2, 2, H('Last Words') + ': Draw 2 cards.')
add('Runeweaver Novice', 3, 5, None, None, H('On Spellboost') + ': Deal 1 damage to all enemy followers.')
add('Duelist of the Reach', 1, 6, 3, 3, H('Clash') + ': Deal 1 damage to the opposing follower.')
add('Swiftwing Skirmisher', 1, 7, 3, 3, H('Strike') + ': Draw a card.')
add('Leyline Conduit', 2, 1, None, None, H('Engage') + ' (1): Draw a card.')
add('Stalwart Recruit', 1, 2, 2, 2, H('Enhance') + ' (4): Give this follower +3/+3.')
add('Tempo Virtuoso', 1, 3, 2, 2, H('Fanfare') + ': ' + H('Combo') + ' (3) - Evolve this follower.')
add('Gravecaller Initiate', 1, 3, 2, 2, H('Fanfare') + ': ' + H('Necromancy') + ' (4) - Give this follower ' + H('Storm') + '.')
add('Sigilwright Adept', 3, 3, None, None, H('Fanfare') + ': ' + H('Earth Rite') + ' (1) - Summon a ' + N('Guardian Golem') + '.')
add('Skybound Vanguard', 1, 5, 3, 3, H('Fanfare') + ': ' + H('Skybound Art') + ' - Deal 5 damage to 2 random enemy followers.')
add('Skybound Ascendant', 1, 7, 4, 4, H('Fanfare') + ': ' + H('Super Skybound Art') + ' - Deal 3 damage to the enemy leader.')
add('Warden of Twilight', 2, 1, None, None, 'At the end of your turn, restore 1 defense to your leader.')
add('Harbinger of Dawn', 2, 1, None, None, 'At the start of your turn, draw a card.')
add('Shepherd of Kin', 1, 9, 2, 2, 'Whenever an allied follower enters the field, give it ' + H('Ward') + '.')
add('Standard-Bearer of the Host', 1, 9, 2, 2, 'Whenever an allied ' + H('Officer') + ' follower enters the field, give this follower +1/+0 until the end of the turn.')
add('Cunning Broker', 1, 11, 2, 2, 'Activates in hand. Whenever you ' + H('Engage') + ' an amulet, draw a card.')
add('Lurking Sentry', 1, 12, 3, 3, 'Activates in deck. At the end of your turn, destroy this card.')

# ==================== 32 动词（全部用官方原句）====================
add('Bounty Hunter', 1, 1, 2, 2, H('Fanfare') + ': Select an enemy follower on the field and destroy it.')
add('Field Commander', 1, 2, 2, 2, H('Fanfare') + ': Select an allied follower on the field and give it ' + H('Rush') + '.')
add('Cinder Bolt', 3, 2, None, None, H('Fanfare') + ': Deal 3 damage to the enemy leader.')
add('Scattershot Volley', 3, 2, None, None, H('Fanfare') + ': Deal 2 damage to a random enemy follower.')
add('Sunder Wave', 3, 3, None, None, H('Fanfare') + ': Deal 7 damage split between all enemy followers.')
add('Soothing Litany', 3, 2, None, None, H('Fanfare') + ': Restore 2 defense to your leader.')
add('Wandering Scholar', 3, 3, None, None, H('Fanfare') + ': Draw 2 cards.')
add('Beastcaller', 1, 4, 2, 2, H('Fanfare') + ': Summon a ' + N('Knight') + '.')
add('Relic Seeker', 1, 3, 2, 2, H('Fanfare') + ': Add a ' + N('Knight') + ' to your hand.')
add('Twin Relic Seeker', 1, 3, 2, 2, H('Fanfare') + ': Add 2 copies of ' + N('Knight') + ' to your hand.')
add('Battle Standard', 1, 2, 2, 2, H('Enhance') + ' (4): Give this follower +3/+3.')
add('Aegis Warden', 1, 2, 1, 1, H('Strike') + ': Give this follower ' + H('Barrier') + '.')
add('Shadow Cloak', 1, 3, 2, 2, H('Enhance') + ' (6): Give this follower +2/+2 and ' + H('Ambush') + '.')
add('Disarming Strike', 1, 3, 2, 2, H('Evolve') + ': Remove ' + H('Ward') + ' from this follower.')
add('Nullifying Beam', 3, 4, None, None, H('Fanfare') + ': Select an enemy follower on the field, remove all abilities from it, and deal it 3 damage.')
add('League Enforcer', 1, 5, 2, 2, H('Super-Evolve') + ': Set the attack of all enemy followers on the field to 4.')
add('Culling Verdict', 3, 5, None, None, H('Fanfare') + ': Set your leader\'s max defense to 1.')
add("Sovereign's Judgment", 3, 5, None, None, H('Fanfare') + ': Halve the enemy leader\'s max defense.')
add('Thrifty Merchant', 3, 5, None, None, H('Fanfare') + ': Halve the cost of all cards in your deck.')
add('Twin-Faced Trickster', 1, 2, 1, 5, 'Whenever you ' + H('Engage') + ' a Clue amulet, swap this follower\'s attack and defense and give it +1/+0.')
add('Studious Apprentice', 3, 2, None, None, H('On Spellboost') + ': Reduce the cost of this card by 1.')
add('Fading Sigil', 2, 2, None, None, H('Engage') + ': Destroy this card.')
add('Purifying Light', 3, 4, None, None, H('Fanfare') + ': Banish all duplicates from your deck.')
add('Meticulous Inquisitor', 3, 4, None, None, H('Evolve') + ': Select an enemy follower on the field with 3 defense or less and banish it.')
add('Reckless Gambler', 3, 1, None, None, H('Fanfare') + ': Discard your hand.')
add('Recall Sigil', 3, 2, None, None, H('Fanfare') + ': Return your hand to deck.')
add('Retreating Scout', 3, 2, None, None, H('Fanfare') + ': Return this card to hand.')
add('Transmuter of Spells', 3, 3, None, None, H('Fanfare') + ': Transform a random spell in your hand into an ' + N('Ersatz Elimination') + '.')
add('Ascendant Mentor', 3, 3, None, None, H('Fanfare') + ': ' + H('Combo') + ' (3) - Evolve this follower.')
add('Deep Leyline', 2, 1, None, None, H('Engage') + ' (1): Gain an earth sigil.')
add('Vessel of Surge', 3, 6, None, None, 'Gain 1 max play point.')
add("Investigator's Verdict", 3, 3, None, None, 'Consume 2 clues to destroy a random enemy follower with the highest attack.')
add('Font of Vigor', 3, 2, None, None, H('Fanfare') + ': Recover 2 play points.')
add('Arcane Attunement', 3, 1, None, None, H('Fanfare') + ': Spellboost your hand 2 times.')
add('Grave Awakening', 3, 4, None, None, H('Fanfare') + ': Reanimate (4).')
add('Sands of Time', 2, 2, None, None, 'Countdown (3) ' + H('Engage') + ' (1): Advance this amulet\'s count by 1.')
add('Lagging Ward', 2, 2, None, None, H('Engage') + ' (1): Delay the count of this amulet by 1.')
add('Portent of Return', 1, 5, 3, 3, 'Activates in deck. At the start of your turn, if allied followers have evolved at least 6 times this match, Invoke this card.')
add('Apocalyptic Ledger', 3, 6, None, None, H('Fanfare') + ': Replace your deck with the Apocalypse Deck.')
add('Echoing Herald', 1, 4, 2, 2, H('Evolve') + ': Replicate the effects of this card\'s Fanfare ability.')
add('Relentless Barrage', 1, 5, 3, 3, H('Evolve') + ': ' + H('Necromancy') + ' (4) - Do this 2 times: "Deal 2 damage to a random enemy follower."')
add('Kaleidoscope Sigil', 3, 6, None, None, H('Fanfare') + ': ' + H('Earth Rite') + ' (2) - Activate 2 random abilities from the following. 1. Summon a ' + N('Guardian Golem') + '. 2. Restore 2 defense to your leader. 3. Gain 3 earth sigils.')
add('Herald of the Mark', 1, 5, 3, 3, H('Fanfare') + ': Gain Crest: ' + N('Test Crest') + '.')
add('Mirror Artisan', 3, 5, None, None, H('Evolve') + ': Select an Artifact follower in your hand that costs 5 or less and summon an exact copy of it.')

# ==================== 条件与模式 ====================
add('Prism Sigil', 3, 4, None, None, H('Fanfare') + ': Select a Mode to activate. 1. Draw a card. 2. Deal 2 damage to the enemy leader.')
add('Convergent Prism', 3, 5, None, None, 'Select a Mode to activate. If there are at least 2 allied cards on the field, activate all of them instead. 1. Draw a card. 2. Restore 2 defense to your leader.')
add('Sequential Ward', 3, 5, None, None, 'At the end of your turn, activate an ability in sequence from the following. 1. Draw a card. 2. Restore 2 defense to your leader.')
add('Tallying Scholar', 3, 4, None, None, H('Fanfare') + ': Deal X damage to the enemy leader. X is the number of cards in your hand.')
add('Kindled Study', 3, 3, None, None, 'X starts at 2. ' + H('On Spellboost') + ': Reduce the cost of this card by 1. Deal X damage to the enemy leader.')
add('Duel of Wits', 3, 4, None, None, H('Fanfare') + ': Restore 2 defense to your leader. ' + H('Super-Evolve') + ': Restore 4 defense instead.')
add('Tidecaller Relic', 3, 3, None, None, H('Fanfare') + ': Deal 2 damage to the enemy leader. If you\'re in ' + H('Overflow') + ', deal 4 damage instead.')
add('Eld Lance Fragment', 3, 2, None, None, 'When this card is discarded, if its cost is 4, add a ' + N('Kindled Blade') + ' to your hand.')
add('Vanguard Surveyor', 3, 3, None, None, H('Fanfare') + ': If there\'s an allied follower on the field with a base cost of 5 or more, draw a card.')
add('Binding Verse', 3, 2, None, None, 'Select an enemy follower on the field, set its defense to 1, and, until the end of your opponent\'s turn, give it "Can\'t attack."')
add('Piercing Verdict', 3, 4, None, None, H('Fanfare') + ': Select an enemy follower on the field with 3 defense or less and banish it.')

# ==================== 分段与格式 ====================
add('Ticking Ward', 2, 2, None, None, 'Countdown (2)' + chr(10) + H('Last Words') + ': Draw a card.')
add('Twofold Sigil', 3, 3, None, None, H('Fanfare') + ': Draw a card.' + chr(10) + 'At the end of your turn, restore 1 defense to your leader.')
add('Metamorphic Adept', 1, 2, 2, 2, H('Fanfare') + ': Draw a card.<ev>' + H('Evolve') + ': Draw 2 cards.<sev>' + H('Super-Evolve') + ': Draw 3 cards.')
add('Relic of Passage', 2, 2, None, None, H('Engage') + ': Destroy this card. Select an allied follower on the field and give it ' + H('Rush') + '.')
add('Whispering Mask', 3, 4, None, None, H('Fanfare') + ': Give this follower "Whenever an allied follower enters the field, draw a card."')
add('Fusion Codifier', 1, 2, 2, 2, H('Fuse') + ': Forestcraft cards' + chr(10) + H('Fanfare') + ': Draw a card.')
add('Nested Prism Sigil', 3, 6, None, None, H('Fanfare') + ': Select a Mode to activate.' + chr(10) +
    '  1. Select a Mode to activate.' + chr(10) +
    '     1. Draw a card.' + chr(10) +
    '     2. Restore 2 defense to your leader.' + chr(10) +
    '  2. Deal 4 damage to all enemy followers.')

# ==================== 战斗关键词（官方裸写形态）====================
add('Bulwark Sentinel', 1, 2, 1, 2, H('Ward'))
add('Gale Runner', 1, 3, 2, 1, H('Storm'))
add('Charging Raider', 1, 2, 2, 1, H('Rush'))
add('Venomtooth Stalker', 1, 4, 1, 3, H('Bane'))
add('Leeching Fiend', 1, 4, 2, 2, H('Drain'))
add('Veiled Prowler', 1, 3, 3, 1, H('Ambush'))
add('Warded Escort', 1, 3, 2, 2, H('Aura'))
add('Grim Overseer', 1, 3, 2, 2, H('Intimidate'))
add('Barrier Sentinel', 1, 4, 2, 3, H('Barrier'))
add('Trial of Many Trials', 1, 5, 3, 3, H('Ward') + ' ' + H('Bane') + ' ' + H('Drain'))

# ==================== 卡名引用（token）====================
add('Knight', 1, 1, 1, 1, '')
add('Guardian Golem', 1, 2, 2, 2, '')
add('Ersatz Elimination', 3, 3, None, None, H('Fanfare') + ': Draw a card.')
add('Kindled Blade', 3, 2, None, None, H('Fanfare') + ': Draw a card.')
add('Sigil of Resolve', 4, 0, None, None, '')

out = {'_schema': 'handcrafted test pool (official phrasing)', 'cards': cards}
d = 'E:/text/reshadow/game/data/cards'
io.open(d + '/testset_cards.json', 'w', encoding='utf-8').write(json.dumps(out, ensure_ascii=False, indent=1))
print('cards: %d -> %d bytes' % (len(cards), os.path.getsize(d + '/testset_cards.json')))

# 词表
lx = [{'id': c['card_id'], 'name': c['name_eng'], 'text': c['eng_text']} for c in cards]
io.open(d + '/testset_lexicon.json', 'w', encoding='utf-8').write(json.dumps(lx, ensure_ascii=False, indent=1))

# 句级语料
import re
sents = []
for c in cards:
    for seg in re.split(r'<hr>|<ev>|</ev>|<sev>|</sev>|\n', c['eng_text']):
        seg = seg.strip()
        if not seg: continue
        for s in re.split(r'(?<=[.!?])\s+', seg):
            s = s.strip()
            if s: sents.append(s)
seen = set(); uniq = []
for s in sents:
    if s in seen: continue
    seen.add(s); uniq.append({'text': s})
io.open(d + '/testset_sentences.json', 'w', encoding='utf-8').write(
    json.dumps([{'group': 'testset', 'sentences': uniq}], ensure_ascii=False, indent=1))
print('lexicon: %d, sentences: %d' % (len(lx), len(uniq)))
for f in ['testset_cards.json','testset_lexicon.json','testset_sentences.json']:
    print('  %-24s %d bytes' % (f, os.path.getsize(d + '/' + f)))
