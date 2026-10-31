# -*- coding: utf-8 -*-
"""生成手工设计的测试卡集（替代 2MB 官方卡池）。

设计原则：
  1. 不用任何原生卡面，全部按官方语法自行拼装。
  2. 每张卡聚焦 1~3 个要素，便于定位失败点。
  3. 每个维度（触发器 / 动词 / 条件 / 子形态）至少覆盖一次。
"""
import io, json, os

K = lambda s: '<b><color=Keyword>%s</color></b>' % s   # 能力头粗体
C = lambda s: '<b><i><color=Keyword>%s</color></i></b>' % s  # 卡名

cards = []
def add(name, kind, cost, atk, life, text, trait='', cls='neutral'):
    cards.append({
        'card_id': 80000000 + len(cards),
        'name_eng': name,
        'type': kind,          # 1=Follower 2=Amulet 3=Spell
        'cost': cost,
        'atk': atk,
        'life': life,
        'class': cls,
        'tribe_name_eng': trait,
        'eng_texts': [text],
        'eng_text': text,
    })

# ============ A. 触发器全谱 ============
add('T Fanfare', 1, 1, 1, 1, K('Fanfare') + ': Draw a card.')
add('T Evolve', 1, 2, 2, 2, K('Evolve') + ': Deal 2 damage to the enemy leader.')
add('T SuperEvolve', 1, 3, 3, 3, K('Super-Evolve') + ': Restore 3 defense to your leader.')
add('T LastWords', 1, 4, 2, 2, K('Last Words') + ': Draw 2 cards.')
add('T Spellboost', 3, 5, None, None, K('On Spellboost') + ': Deal 1 damage to all enemy followers.')
add('T Clash', 1, 6, 3, 3, K('Clash') + ': Deal 1 damage to the opposing follower.')
add('T Strike', 1, 7, 3, 3, K('Strike') + ': Draw a card.')
add('T Engage', 2, 1, None, None, K('Engage') + ' (1): Draw a card.')
add('T Fuse', 1, 8, 2, 2, K('Fuse') + ': Forestcraft cards. ' + K('Fanfare') + ': Draw a card.')
add('T TurnStart', 2, 1, None, None, 'At the start of your turn, draw a card.')
add('T TurnEnd', 2, 1, None, None, 'At the end of your turn, restore 1 defense to your leader.')
add('T WheneverEnter', 1, 9, 2, 2, 'Whenever an allied follower enters the field, give it ' + K('Ward') + '.')
add('T WhenDamaged', 1, 10, 3, 3, 'When this follower takes damage, draw a card.')
add('T InHand', 1, 11, 2, 2, 'Activates in hand. Whenever you ' + K('Engage') + ' an amulet, draw a card.')
add('T InDeck', 1, 12, 3, 3, 'Activates in deck. At the end of your turn, destroy this card.')

# ============ B. 32 动词全谱 ============
add('V Select Destroy', 1, 1, 2, 2, K('Fanfare') + ': Select an enemy follower on the field and destroy it.')
add('V Deal Leader', 3, 2, None, None, K('Fanfare') + ': Deal 3 damage to the enemy leader.')
add('V Deal Split', 3, 3, None, None, K('Fanfare') + ': Deal 4 damage split between all enemies.')
add('V Restore', 3, 2, None, None, K('Fanfare') + ': Restore 2 defense to your leader.')
add('V Draw X', 3, 3, None, None, K('Fanfare') + ': Draw 2 cards.')
add('V Summon', 1, 4, 2, 2, K('Fanfare') + ': Summon a ' + C('Fighter') + '.')
add('V Add', 1, 3, 2, 2, K('Fanfare') + ': Add a ' + C('Fighter') + ' to your hand.')
add('V Give Stats', 1, 2, 2, 2, K('Fanfare') + ': Give this follower +2/+2.')
add('V Give Keyword', 1, 2, 1, 1, K('Fanfare') + ': Give this follower ' + K('Ward') + '.')
add('V Remove', 1, 3, 2, 2, K('Evolve') + ': Remove ' + K('Ward') + ' from this follower.')
add('V Set Defense', 3, 4, None, None, K('Fanfare') + ': Set the defense of all enemy followers on the field to 1.')
add('V Halve', 3, 5, None, None, K('Fanfare') + ': Halve the enemy leader\'s max defense.')
add('V Swap', 1, 2, 1, 5, K('Fanfare') + ': Swap this follower\'s attack and defense.')
add('V ReduceCost', 1, 3, 2, 2, K('Fanfare') + ': Reduce the cost of a card in your hand by 1.')
add('V Destroy', 1, 5, 2, 2, K('Fanfare') + ': Destroy an enemy amulet.')
add('V Banish', 3, 4, None, None, K('Fanfare') + ': Banish a random enemy follower from the field.')
add('V Discard', 3, 1, None, None, K('Fanfare') + ': Discard a card. Draw a card.')
add('V Return', 3, 2, None, None, K('Fanfare') + ': Return an enemy follower to its hand.')
add('V Transform', 3, 3, None, None, K('Fanfare') + ': Transform an enemy follower into a ' + C('Fighter') + '.')
add('V EvolveEffect', 3, 3, None, None, K('Fanfare') + ': Evolve an allied follower.')
add('V GainResource', 1, 2, 1, 1, K('Fanfare') + ': Gain an earth sigil.')
add('V Consume', 1, 4, 2, 2, K('Fanfare') + ': Consume a clue to draw a card.')
add('V Recover', 3, 2, None, None, K('Fanfare') + ': Recover 2 play points.')
add('V SpellboostVerb', 3, 1, None, None, K('Fanfare') + ': Spellboost your hand.')
add('V Reanimate', 3, 4, None, None, K('Fanfare') + ': Reanimate (3).')
add('V Advance', 2, 2, None, None, 'Countdown (3) ' + K('Engage') + ': Advance this amulet\'s count by 1.')
add('V Delay', 3, 3, None, None, K('Fanfare') + ': Delay the counts of all your crests by 1.')
add('V Invoke', 1, 5, 3, 3, 'Activates in deck. At the start of your turn, Invoke this card.')
add('V Replace', 3, 6, None, None, K('Fanfare') + ': Replace your deck with the Apocalypse Deck.')
add('V Replicate', 1, 4, 2, 2, K('Evolve') + ': Replicate the effects of this card\'s Fanfare ability.')
add('V DoTimes', 3, 5, None, None, K('Fanfare') + ': Do this 2 times: "Deal 1 damage to the enemy leader."')
add('V Activate', 3, 6, None, None, K('Fanfare') + ': Activate 2 random abilities from the following. 1. Draw a card. 2. Deal 2 damage to the enemy leader. 3. Restore 2 defense to your leader.')
add('V GainCrest', 1, 5, 3, 3, K('Fanfare') + ': Gain Crest: ' + C('Test Crest') + '.')
add('V CopyExact', 3, 5, None, None, K('Fanfare') + ': Summon an exact copy of an allied follower.')

# ============ C. 条件与机制 ============
add('M Combo', 1, 3, 2, 2, K('Fanfare') + ': ' + K('Combo') + ' (3) - Draw 2 cards.')
add('M Enhance', 1, 2, 2, 2, K('Fanfare') + ': Draw a card. ' + K('Enhance') + ' (4): Draw 2 cards.')
add('M Necromancy', 1, 3, 2, 2, K('Fanfare') + ': ' + K('Necromancy') + ' (3): Draw a card.')
add('M EarthRite', 3, 3, None, None, K('Fanfare') + ': ' + K('Earth Rite') + ' (2) - Deal 4 damage to all enemy followers.')
add('M Skybound', 1, 5, 3, 3, K('Skybound Art') + ' - Deal 2 damage to the enemy leader.')
add('M SuperSkybound', 1, 7, 4, 4, K('Super Skybound Art') + ' - Deal 3 damage to the enemy leader.')
add('M Overflow', 3, 3, None, None, K('Fanfare') + ': Draw a card. If you\'re in ' + K('Overflow') + ', draw 2 cards instead.')
add('M Mode', 3, 4, None, None, K('Fanfare') + ': Select a Mode to activate. 1. Draw a card. 2. Deal 2 damage to the enemy leader.')
add('M ModeAll', 3, 5, None, None, K('Fanfare') + ': Select a Mode to activate. If you have 5 play points, activate all of them instead. 1. Draw a card. 2. Restore 2 defense to your leader.')
add('M VariableX', 3, 4, None, None, K('Fanfare') + ': Deal X damage to the enemy leader. X is the number of cards in your hand.')
add('M XStarts', 3, 3, None, None, 'X starts at 2. ' + K('On Spellboost') + ': Increase X by 1. Deal X damage to the enemy leader.')
add('M Instead', 3, 4, None, None, K('Fanfare') + ': Restore 2 defense to your leader. ' + K('Super-Evolve') + ': Restore 4 defense instead.')
add('M CondCost', 3, 2, None, None, K('Fanfare') + ': If its cost is 3, draw a card.')
add('M CondBaseCost', 3, 3, None, None, K('Fanfare') + ': If there\'s an allied follower on the field with a base cost of 3 or more, draw a card.')
add('M UntilEnd', 3, 2, None, None, K('Fanfare') + ': Give an enemy follower "Can\'t attack." until the end of your opponent\'s turn.')

# ============ D. 分段与格式 ============
add('F Countdown', 2, 2, None, None, 'Countdown (2)' + chr(10) + K('Last Words') + ': Draw a card.')
add('F MultiAbility', 3, 3, None, None, K('Fanfare') + ': Draw a card.' + chr(10) + 'At the end of your turn, restore 1 defense to your leader.')
add('F ThreeDomains', 1, 2, 2, 2, K('Fanfare') + ': Draw a card.<ev>' + K('Evolve') + ': Draw 2 cards.<sev>' + K('Super-Evolve') + ': Draw 3 cards.')
add('F SharedHeader', 2, 2, None, None, K('Engage') + ': Destroy this card. Select an allied follower on the field and give it ' + K('Rush') + '.')
add('F QuotedInner', 3, 4, None, None, K('Fanfare') + ': Give this follower "Whenever an allied follower enters the field, draw a card."')
add('F NestedMode', 3, 6, None, None, K('Fanfare') + ': Select a Mode to activate.' + chr(10) +
    '  1. Select a Mode to activate.' + chr(10) +
    '     1. Draw a card.' + chr(10) +
    '     2. Restore 2 defense to your leader.' + chr(10) +
    '  2. Deal 4 damage to all enemy followers.')

# ============ E. 战斗相关 ============
add('B Ward', 1, 2, 1, 2, K('Ward'))
add('B Storm', 1, 3, 2, 1, K('Storm'))
add('B Rush', 1, 2, 2, 1, K('Rush'))
add('B Bane', 1, 4, 1, 3, K('Bane'))
add('B Drain', 1, 4, 2, 2, K('Drain'))
add('B Ambush', 1, 3, 3, 1, K('Ambush'))
add('B Aura', 1, 3, 2, 2, K('Aura'))
add('B Intimidate', 1, 3, 2, 2, K('Intimidate'))
add('B Barrier', 1, 4, 2, 3, K('Barrier'))
add('B Multi', 1, 5, 3, 3, K('Ward') + ' ' + K('Bane') + ' ' + K('Drain'))

# ============ F. Token ============
add('Fighter', 1, 1, 1, 1, '')
add('Ghost', 1, 1, 1, 1, '')
add('Test Crest', 4, 0, None, None, '')

out = {'_schema': 'handcrafted-testset', 'cards': cards}
path = 'E:/text/reshadow/game/data/cards/testset_cards.json'
io.open(path, 'w', encoding='utf-8').write(json.dumps(out, ensure_ascii=False, indent=1))
print('written %d cards -> %s' % (len(cards), path))
print('size: %d bytes' % os.path.getsize(path))
