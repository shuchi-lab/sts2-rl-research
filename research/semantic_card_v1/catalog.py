"""Source-reviewed ordered mechanics programs, with shared operation semantics.

Card names below are dispatch keys only. ``descriptor_arrays`` emits no keys, IDs,
source text, names, or per-card categorical value. Unknown cards/rules are errors.
Source metadata is built offline from the original v0.111.0 declarations; current
arithmetic is always supplied by the native bridge, never evaluated here.
"""
from __future__ import annotations
import copy,json
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
import numpy as np
from .schema import *

class UnsupportedSemantics(ValueError):
    """A card or mechanic is outside this catalog snapshot."""

def state(ref,coefficient=1,**kw):return dict(op='state',ref=ref,coefficient=coefficient,**kw)
def constant(value):return dict(op='constant',value=value,known=1)
def expression(op,*args):
    return [dict(op=op,arity=len(args))]+[t for arg in args for t in (arg if isinstance(arg,list)else[arg])]
def step(op,amount=None,**kw):
    out=dict(op=op,**kw)
    if isinstance(amount,str):
        out.setdefault('terms',[]).append(state(amount));out['uses_instance_amount']=amount=='instance_amount';out['uses_instance_secondary']=amount=='instance_secondary'
    elif isinstance(amount,list):out.setdefault('terms',[]).extend(amount)
    elif amount is not None:out['amount']=amount
    return out

def damage(a,target='enemy',count=1,**kw):return step('damage',a,target=target,count=count,powered=True,**kw)
def block(a,**kw):return step('block',a,target='self',powered=True,**kw)
def status(prop,a,target='self',**kw):return step('status',a,target=target,property=prop,operator='add',**kw)
def rule(event,payload,**kw):return [step('rule_begin',event=event,scope=kw.pop('scope','combat'),**kw)]+payload+[step('rule_end')]
def condition(terms,payload,**kw):return [step('condition_begin',terms=terms,conditional=True,**kw)]+payload+[step('condition_end')]
def modify(prop,a,**kw):return step('modify',a,property=prop,operator=kw.pop('operator','add'),target=kw.pop('target','self'),**kw)
def move(source,destination,filter='any',selection='chosen',count=1,**kw):return step('move',source=source,destination=destination,filter=filter,selection=selection,count=count,target='chosen_card'if selection=='chosen'else'all_matching_cards',**kw)
def exhaust(count=1,selection='chosen',filter='any',**kw):return step('exhaust',source='hand',destination='exhaust',count=count,selection=selection,filter=filter,target='chosen_card'if selection=='chosen'else'all_matching_cards',**kw)
def draw(a,**kw):return step('draw',a,target='self',source='draw',destination='hand',**kw)

# Card-generated powers use a common amount variable in their active descriptors.
# Mapping aliases is engine routing metadata; none are emitted as learned categories.
POWER_ALIASES={'CrimsonMantleBlock':'CrimsonMantle','TheBombPower':'TheBomb','AutomationPower':'Automation','PanachePower':'Panache','RetainHand':'RetainHand','StrengthUp':'Strength','StrengthDown':'Strength','DexterityUp':'Dexterity'}

def power_program(name,a='instance_amount',secondary='instance_secondary'):
    n=name.removesuffix('Power');n=POWER_ALIASES.get(name,POWER_ALIASES.get(n,n))
    if n=='Thorns':return rule('incoming_attack',[step('damage',a,target='enemy',unpowered=True)],phase='before_damage')
    if n=='Intangible':return [modify('damage_received',1,operator='clamp',scope='turn',uses_instance_amount=True)]
    if n=='HelloWorld':return rule('turn_start',[step('generate',a,source='ironclad_pool',filter='common',destination='hand',distinct=True)],phase='before_draw')
    if n in ('NextTurnEnergy','EnergyNextTurn'):return rule('turn_start',[step('energy',a,target='self'),step('remove_rule')],phase='before_draw',limit=1)
    if n in ('NextTurnDraw','DrawCardsNextTurn'):return rule('turn_start',[draw(a),step('remove_rule')],phase='before_draw',limit=1)
    if n=='Rebound':return [modify('destination',None,operator='set',destination='draw_top',scope='turn',uses_instance_amount=True)]
    if n=='ToricToughness':return rule('turn_start',[step('block',secondary,target='self',unpowered=True)],phase='after',uses_instance_amount=True,uses_instance_secondary=True)
    if n=='Curious':return [modify('cost',a,operator='subtract',filter='power',minimum=0)]
    if n=='Improvement':return rule('combat_start',[step('upgrade',a,selection='random',source='all_piles',filter='upgradeable',scope='permanent')],phase='combat_end')
    if n=='Strangle':return rule('card_play',[step('lose_hp',a,target='target_owner',unpowered=True)],scope='turn',phase='after',skip_first=True)
    if n=='Strength':return [modify('damage',a,powered=True)]
    if n=='Dexterity':return [modify('block',a,powered=True)]
    if n in ('Vulnerable','Weak','Frail'):
        return [modify({'Vulnerable':'damage_received','Weak':'damage','Frail':'block'}[n],1.5 if n=='Vulnerable'else.75,operator='multiply',powered=True,scope='turn',uses_instance_amount=True)]
    if n=='TemporaryStrength':return rule('turn_end',[modify('strength',a,operator='subtract'),step('remove_rule')])
    if n=='Vigor':return [modify('damage',a,powered=True,scope='next_attack')]
    if n=='Aggression':return rule('turn_start',[move('discard','hand','attack','random',amount=a),step('upgrade',target='chosen_card')],phase='before_draw')
    if n=='Automation':return rule('draw',[step('energy',a,target='self')],threshold=10,initial=10,condition='every_n',uses_instance_progress=True)
    if n=='Barricade':return [modify('block_retention',1,operator='preserve')]
    if n=='BlockNextTurn':return rule('turn_start',[step('block',a,target='self',unpowered=True),step('remove_rule')],phase='after',limit=1)
    if n=='Calamity':return rule('attack_play',[step('generate',a,filter='attack',source='ironclad_pool',destination='hand',with_replacement=True)],phase='after')
    if n=='Colossus':return [modify('damage_received',.5,operator='multiply',powered=True,condition='target_vulnerable',scope='turn',duration=a if isinstance(a,(float,int))else None,uses_instance_amount=True)]
    if n=='Corruption':return [modify('cost',0,operator='set',filter='skill'),modify('destination',target='event_card',a=None,operator='set',filter='skill',destination='exhaust')]
    if n=='CrimsonMantle':return rule('turn_start',[step('lose_hp',secondary,target='self',unpowered=True),step('block',a,target='self',unpowered=True)],phase='after_draw')
    if n=='Cruelty':return [modify('vulnerable_multiplier',a,condition='target_vulnerable',terms=[constant(.01)],powered=True)]
    if n=='DarkEmbrace':return rule('exhaust',[draw(a)],phase='after')+rule('exhaust',[draw(a)],filter='ethereal',phase='end_turn_flush',condition='owner_turn')
    if n in ('DarkShackles','Mangle','SetupStrike'):
        sign=-1 if n!='SetupStrike'else 1
        return [modify('strength',a,operator='subtract'if sign<0 else'add',scope='turn',temporary=True),step('rule_begin',event='turn_end',scope='turn'),modify('strength',a,operator='add'if sign<0 else'subtract'),step('remove_rule'),step('rule_end')]
    if n=='DemonForm':return rule('turn_start',[status('strength',a)],phase='before_draw')
    if n=='Entropy':return rule('turn_start',[step('transform',a,source='hand',selection='chosen',pool='ironclad',destination='hand')],phase='after_draw')
    if n=='Fasten':return [modify('block',a,filter='defend',powered=True)]
    if n=='FeelNoPain':return rule('exhaust',[step('block',a,target='self',unpowered=True,not_card_block=True)])
    if n=='FlameBarrier':return rule('incoming_attack',[step('damage',a,target='enemy',unpowered=True)],scope='turn',phase='after_damage')
    if n=='FreeAttack':return [modify('cost',0,operator='set',filter='attack',scope='next_attack',uses_instance_amount=True)]
    if n=='Hellraiser':return rule('draw',[step('autoplay',target='event_card',source='event_card',cost_free=True)],filter='strike',phase='before')
    if n=='Inferno':return rule('turn_start',[step('lose_hp',secondary,target='self',unpowered=True)],phase='after_draw')+rule('unblocked_damage',[step('damage',a,target='all_enemies',unpowered=True)],condition='owner_turn')
    if n=='Juggernaut':return rule('block_gain',[step('damage',a,target='random_enemy',unpowered=True,stochastic=True)],condition='counter_reached',threshold=0)
    if n=='Juggling':return rule('attack_play',[step('clone',a,source='event_card',reference='event',destination='hand')],phase='before',threshold=3,condition='counter_reached',reset_turn=True,limit=1,terms=[state('attacks_played_turn')])
    if n=='Mayhem':return rule('turn_start',[step('autoplay',a,source='draw',selection='top',cost_free=True)],phase='after_draw')
    if n=='NoBlock':return [modify('block_permission',0,operator='prevent',powered=True,scope='turn',uses_instance_amount=True)]
    if n=='NoDraw':return [modify('draw_permission',0,operator='prevent',scope='turn',condition='not_initial_draw')]
    if n=='Nostalgia':return [modify('destination',None,operator='set',filter='attack_or_skill',destination='draw_top',condition='first_event',uses_instance_amount=True,terms=expression('less',state('cards_played_turn'),state('instance_amount')),reset_turn=True)]
    if n=='OneTwoPunch':return [modify('play_count',1,filter='attack',scope='turn',uses_instance_amount=True,limit=a if isinstance(a,(float,int))else None)]
    if n=='Panache':return rule('card_finished',[step('damage',a,target='all_enemies',unpowered=True)],condition='every_n',threshold=5,initial=5,reset_turn=True,skip_first=True,uses_instance_progress=True)
    if n=='Plating':return rule('turn_end',[step('block',a,target='self',unpowered=True)],phase='before')+rule('turn_start',[step('status',-1,property='plating',target='self')],skip_first=True)
    if n=='PrepTime':return rule('turn_start',[modify('damage',a,powered=True,scope='next_attack')],phase='before_draw')
    if n=='Pyre':return [step('max_energy',a,target='self')]
    if n=='Rage':return rule('attack_play',[step('block',a,target='self',unpowered=True)],scope='turn',phase='after')
    if n=='RetainHand':return [modify('hand_retention',1,operator='preserve',scope='turn',uses_instance_amount=True)]
    if n=='RollingBoulder':return rule('turn_start',[step('damage',a,target='all_enemies',unpowered=True),modify('damage',5,target='source_card',scope='combat')],phase='after_draw',uses_instance_progress=True)
    if n=='Rupture':return rule('unblocked_damage',[status('strength',a)],condition='owner_turn',phase='card_completion')
    if n=='Stampede':return rule('turn_end',[step('autoplay',a,source='hand',filter='attack',selection='random',cost_free=True)],phase='before')
    if n=='Stratagem':return rule('shuffle',[move('draw','hand',amount=a)],phase='after')
    if n=='TheBomb':return rule('turn_end',[step('damage',secondary,target='all_enemies',unpowered=True),step('remove_rule')],phase='before',threshold=a if isinstance(a,(int,float))else None,uses_instance_amount=True,uses_instance_secondary=True,limit=1)
    if n=='TheGambit':return rule('unblocked_damage',[step('kill',target='self'),step('remove_rule')],powered=True,limit=1)
    if n=='Unmovable':return [modify('block',2,operator='multiply',powered=True,condition='first_event',uses_instance_amount=True,reset_turn=True,terms=[state('qualifying_events')])]
    if n=='Vicious':return rule('vulnerable_apply',[draw(a)],condition='counter_reached',threshold=0)
    if n=='Disintegration':return rule('turn_end',[step('damage',a,target='self',unpowered=True)],phase='after')
    if n=='MindRot':return rule('turn_start',[draw(a,operator='subtract')],phase='before_draw')
    if n=='Sloth':return [modify('unplayable',1,operator='set',condition='counter_reached',uses_instance_amount=True,reset_turn=True,terms=[state('cards_played_turn')])]
    if n=='WasteAway':return [step('max_energy',a,target='self',operator='subtract')]
    raise UnsupportedSemantics(f'no reviewed semantic power: {name}')

POWER_CARD_KEYS={
 'Aggression':('Aggression',1),'Barricade':('Barricade',1),'Corruption':('Corruption','Power'),
 'CrimsonMantle':('CrimsonMantle','CrimsonMantlePower'),'Cruelty':('Cruelty','CrueltyPower'),
 'DarkEmbrace':('DarkEmbrace',1),'DemonForm':('DemonForm','Strength'),'FeelNoPain':('FeelNoPain','Power'),
 'Hellraiser':('Hellraiser',1),'Inferno':('Inferno','InfernoPower'),'Juggernaut':('Juggernaut','JuggernautPower'),
 'Juggling':('Juggling',1),'Pyre':('Pyre','Energy'),'Rupture':('Rupture','Strength'),'Stampede':('Stampede','Power'),
 'StoneArmor':('Plating','Plating'),'Unmovable':('Unmovable',1),'Vicious':('Vicious','Cards'),
 'Automation':('Automation','Energy'),'Calamity':('Calamity',1),'Entropy':('Entropy','Cards'),
 'EternalArmor':('Plating','Plating'),'Fasten':('Fasten','ExtraBlock'),'Mayhem':('Mayhem',1),
 'Nostalgia':('Nostalgia',1),'Panache':('Panache','PanacheDamage'),'PrepTime':('PrepTime','PrepTime'),
 'RollingBoulder':('RollingBoulder','RollingBoulder'),'Stratagem':('Stratagem',1),
}

def card_program(row,upgraded=False):
    n=row['name'];v=row['upgraded_scalars'if upgraded else'scalars'];u=int(upgraded)
    def val(k):
        try:return v[k]
        except KeyError:raise UnsupportedSemantics(f'{n} missing scalar {k}')from None
    def d(**kw):return damage(val('Damage'),**kw)
    def b(**kw):return block(val('Block'),**kw)
    def dr(**kw):return draw(val('Cards'),**kw)
    if n=='Splash':raise UnsupportedSemantics('Splash generates foreign-character attacks outside admitted scope')
    if n in POWER_CARD_KEYS:
        power,key=POWER_CARD_KEYS[n];a=val(key)if isinstance(key,str)else key
        return power_program(power,a,1 if power in ('CrimsonMantle','Inferno')else 'instance_secondary')
    if n in ('Bludgeon','StrikeIronclad','UltimateStrike','GiantRock','Shiv'):return [d()]
    if n in ('DefendIronclad','Impervious','UltimateDefend'):return [b()]
    if n=='Anger':return [d(),step('clone',1,source='current_card',reference='self',destination='discard')]
    if n=='Armaments':return [b(),step('upgrade',source='hand',filter='upgradeable',selection='all'if u else'chosen',count=None if u else 1)]
    if n in ('AshenStrike','BodySlam','Bully','ExpectAFight','PerfectedStrike','GoldAxe','MindBlast','Rend'):
        ref={'AshenStrike':'exhaust_count','BodySlam':'block','Bully':'target_vulnerable','ExpectAFight':'strength','PerfectedStrike':'strike_count','GoldAxe':'cards_played_combat','MindBlast':'draw_count','Rend':'target_debuff_count'}[n]
        coefficient=val('CalculationExtra'if n=='ExpectAFight'else'ExtraDamage')
        formula=expression('add',constant(val('CalculationBase')),expression('multiply',constant(coefficient),state(ref)))
        return [block(formula)if n=='ExpectAFight'else damage(formula)]
    if n in ('Bash','Break'):return [d(),status('vulnerable',val('Vulnerable'),'enemy',scope='turn')]
    if n=='BattleTrance':return [dr()]+power_program('NoDraw',1)
    if n in ('BloodWall','Hemokinesis','Breakthrough'):
        return [step('lose_hp',val('HpLoss'),target='self',unpowered=True),b()if n=='BloodWall'else d(target='all_enemies'if n=='Breakthrough'else'enemy')]
    if n=='Bloodletting':return [step('lose_hp',val('HpLoss'),target='self',unpowered=True),step('energy',val('Energy'),target='self')]
    if n=='Brand':return [step('lose_hp',val('HpLoss'),target='self',unpowered=True),exhaust(),status('strength',val('Strength'))]
    if n=='BurningPact':return [exhaust(),dr(phase='after_selection')]
    if n=='Cascade':return [step('autoplay',expression('add',state('spent_x'),constant(u)),source='draw',selection='top',cost_free=True)]
    if n=='Cinder':return [d(),exhaust(selection='random',stochastic=True)]
    if n=='Colossus':return [b()]+power_program('Colossus',val('Colossus'))
    if n=='Conflagration':return [d(target='all_enemies',count=val('Repeat'))]
    if n=='Dismantle':return [d(count=1,conditional=True,terms=expression('add',constant(1),expression('greater',state('target_vulnerable'),constant(0))))]
    if n=='Dominate':return [status('vulnerable',val('Vulnerable'),'enemy'),status('strength',[state('target_vulnerable',live=1)])]
    if n=='DrumOfBattle':return [dr()]+rule('self_exhaust',[step('energy',val('Energy'),target='self')],terms=[state('play_count')])
    if n=='EvilEye':return [b(count=1,conditional=True,terms=expression('add',constant(1),expression('greater',state('cards_exhausted_turn'),constant(0))))]
    if n=='Feed':return [d()]+condition([], [step('max_hp',val('MaxHp'),target='self',scope='permanent')],condition='killed_target')
    if n=='FiendFire':return [step('group_begin',snapshot=True,terms=[state('hand_count',snapshot=1)]),exhaust(count=None,selection='all',snapshot=True),d(count=None,terms=[state('snapshot_count')]),step('group_end')]
    if n=='FightMe':return [d(count=val('Repeat')),status('strength',val('Strength')),status('strength',val('EnemyStrength'),'enemy')]
    if n=='FlameBarrier':return [b()]+power_program('FlameBarrier',val('DamageBack'))
    if n in ('ForgottenRitual','Production','Fuel'):return [step('energy',val('Energy'),target='self')]
    if n=='Havoc':return [step('autoplay',1,source='draw',selection='top',destination='exhaust',cost_free=True,self_exhaust=True)]
    if n=='Headbutt':return [d(),move('discard','draw_top')]
    if n=='HowlFromBeyond':return [d(target='all_enemies')]+rule('turn_end',[step('autoplay',target='source_card',source='current_card',cost_free=True)],condition='card_is_exhausted',phase='before')
    if n=='InfernalBlade':return [step('generate',1,source='ironclad_pool',filter='attack',selection='random',destination='hand',cost_free=True,scope='turn',stochastic=True)]
    if n=='Inflame':return [status('strength',val('Strength'))]
    if n=='IronWave':return [b(),d()]
    if n=='Mangle':return [d()]+power_program('Mangle',val('StrengthLoss'))
    if n=='MoltenFist':return [d()]+condition([], [status('vulnerable',[state('target_vulnerable',live=1)],'enemy')],condition='target_survives')
    if n=='NotYet':return [step('heal',val('Heal'),target='self')]
    if n=='Offering':return [step('lose_hp',val('HpLoss'),target='self',unpowered=True),step('energy',val('Energy'),target='self'),dr()]
    if n=='OneTwoPunch':return power_program(n,val('Attacks'))
    if n=='PactsEnd':return condition(expression('greater_equal',state('exhaust_count'),constant(val('Cards'))),[d(target='all_enemies')])
    if n=='Pillage':return [d(),draw(1,filter='non_attack',condition='counter_reached',repeat_each=True,maximum=10)]
    if n in ('PommelStrike','FlashOfSteel'):return [d(),dr()]
    if n=='PrimalForce':return [step('group_begin',source='hand',filter='attack',snapshot=True),step('transform',source='hand',filter='attack',selection='all',upgraded=bool(u)),damage(24 if u else 20,target='chosen_card'),step('group_end')]
    if n=='Rage':return power_program(n,val('Power'))
    if n=='Rampage':return [d(),modify('damage',val('Increase'),target='source_card',scope='combat')]
    if n=='SecondWind':return [step('group_begin',source='hand',filter='non_attack',snapshot=True,scope='per_matching_card'),exhaust(selection='all',filter='non_attack',snapshot=True),b(),step('group_end')]
    if n=='SetupStrike':return [d()]+power_program(n,val('Strength'))
    if n in ('ShrugItOff','Finesse'):return [b(),dr()]
    if n=='Spite':return [d(count=1,conditional=True,terms=expression('add',constant(1),expression('multiply',constant(val('Repeat')-1),expression('greater',state('unblocked_damage_turn'),constant(0)))))]
    if n=='Stoke':return [step('group_begin',snapshot=True,terms=[state('hand_count',snapshot=1)]),exhaust(count=None,selection='all',snapshot=True),step('generate',[state('snapshot_count')],source='ironclad_pool',destination='hand',upgraded=bool(u),with_replacement=True),step('group_end')]
    if n=='Stomp':return [d(target='all_enemies')]+rule('attack_play',[modify('cost',-1,target='source_card',scope='turn')],phase='before',terms=[state('attacks_played_turn')])
    if n in ('SwordBoomerang','Volley','Whirlwind'):
        return [d(target='all_enemies'if n=='Whirlwind'else'random_enemy',count=val('Repeat')if n=='SwordBoomerang'else None,terms=[]if n=='SwordBoomerang'else[state('spent_x')],stochastic=n!='Whirlwind')]
    if n=='Taunt':return [b(),status('vulnerable',val('Vulnerable'),'enemy')]
    if n=='TearAsunder':return [d(count=None,terms=expression('add',constant(1),state('hp_loss_events_combat')))]
    if n=='Thrash':return [d(count=2),step('group_begin',source='hand',filter='attack',selection='random',stochastic=True),modify('damage',[state('source_damage')],target='source_card',scope='combat'),exhaust(filter='attack',selection='random'),step('group_end')]
    if n=='Thunderclap':return [d(target='all_enemies'),status('vulnerable',val('Vulnerable'),'all_enemies')]
    if n=='Tremble':return [status('vulnerable',val('Vulnerable'),'enemy')]
    if n=='TrueGrit':return [b(),exhaust(selection='chosen'if u else'random',stochastic=not bool(u))]
    if n=='TwinStrike':return [d(count=2)]
    if n=='Unrelenting':return [d()]+power_program('FreeAttack',1)
    if n=='Uppercut':return [d(),status('weak',val('Power'),'enemy'),status('vulnerable',val('Power'),'enemy')]
    if n=='Alchemize':return [step('potion',1,destination='inventory',stochastic=True)]
    if n=='Anointed':return [move('draw','hand','rare','random',None,terms=[state('hand_capacity')])]
    if n=='BeatDown':return [step('autoplay',val('Cards'),source='discard',filter='attack',selection='random',cost_free=True,distinct=True,stochastic=True)]
    if n in ('Bolas','ThrummingHatchet'):return [d()]+rule('turn_start',[move('current_card','hand',selection='all')],phase='before_draw',terms=[state('last_turn_matching_cards')],reference='self',condition='counter_reached',threshold=0)
    if n=='Catastrophe':return [step('autoplay',val('Cards'),source='draw',filter='playable',selection='random',cost_free=True,live=True,stochastic=True)]
    if n=='DarkShackles':return power_program(n,val('StrengthLoss'))
    if n=='Discovery':return [step('generate',1,source='ironclad_pool',selection='choose_from_random',count=3,distinct=True,can_skip=True,cost_free=True,scope='turn',destination='hand')]
    if n=='DramaticEntrance':return [d(target='all_enemies')]
    if n=='Equilibrium':return [b()]+power_program('RetainHand',val('Equilibrium'))
    if n=='Fisticuffs':return [d(),block([state('damage_dealt')],conditional=True)]
    if n=='HandOfGreed':return [d()]+condition([],[step('gold',val('Gold'),target='self')],condition='killed_target')
    if n=='HiddenGem':return [modify('play_count',val('Replay'),target='random_card',source='draw',filter='playable',selection='random',scope='combat',terms=expression('less',state('instance_replay'),constant(1)),stochastic=True)]
    if n=='Impatience':return condition(expression('equal',state('attacks_in_hand'),constant(0)),[dr()])
    if n=='JackOfAllTrades':return [step('generate',val('Cards'),source='colorless_pool',filter='not_source',distinct=True,destination='hand',stochastic=True)]
    if n=='Jackpot':return [d(),step('generate',val('Cards'),source='ironclad_pool',filter='zero_cost',with_replacement=True,destination='hand',upgraded=bool(u),stochastic=True)]
    if n=='MasterOfStrategy':return [dr()]
    if n=='Omnislice':return [d(),step('damage',[state('damage_dealt')],target='all_enemies',filter='not_source',unpowered=True,conditional=True)]
    if n=='PanicButton':return [b()]+power_program('NoBlock',val('Turns'))
    if n=='Prolong':return rule('turn_start',[step('block',[state('block',snapshot=1)],target='self',unpowered=True)],limit=1,phase='after',snapshot=True)
    if n=='Prowess':return [status('strength',val('Strength')),status('dexterity',val('Dexterity'))]
    if n=='Purity':return [exhaust(count=val('Cards'),can_skip=True,minimum=0,maximum=val('Cards'))]
    if n=='Restlessness':return condition(expression('equal',state('hand_count'),constant(0)),[dr(),step('energy',val('Energy'),target='self')])
    if n=='Salvo':return [d()]+power_program('RetainHand',1)
    if n=='Scrawl':return [draw([state('hand_capacity')])]
    if n in ('SecretTechnique','SecretWeapon'):return [move('draw','hand','skill'if n=='SecretTechnique'else'attack')]
    if n=='SeekerStrike':return [d(),move('draw','hand',selection='choose_from_random',maximum=val('Cards'),count=1,snapshot=True)]
    if n=='Shockwave':return [step('group_begin',target='all_enemies',scope='per_matching_card'),status('weak',val('Power'),'enemy'),status('vulnerable',val('Power'),'enemy'),step('group_end')]
    if n=='TheBomb':return power_program(n,val('Turns'),val('BombDamage'))
    if n=='TheGambit':return [b()]+power_program(n,1)
    if n=='ThinkingAhead':return [dr(),move('hand','draw_top')]
    # Colorless EventCardPool and combat-inert QuestCardPool acquisition closure.
    if n=='Luminesce':return [step('energy',val('Energy'),target='self')]
    if n=='Abundance':return [step('generate',1,source='ironclad_pool',filter='power',selection='choose_from_random',count=3,distinct=True,cost_free=True,scope='turn',destination='hand',upgraded=True)]
    if n=='Apotheosis':return [step('upgrade',source='all_piles',filter='upgradeable',selection='all')]
    if n=='Apparition':return [status('intangible',val('Intangible'),scope='turn')]
    if n=='BrightestFlame':return [step('energy',val('Energy'),target='self'),dr(),step('max_hp',-val('MaxHp'),target='self',scope='permanent')]
    if n=='ByrdSwoop':return [d()]
    if n=='Caltrops':return power_program('Thorns',val('Thorns'))
    if n=='Clash':return condition(expression('equal',state('non_attacks_in_hand'),constant(0)),[d()])
    if n=='Distraction':return [step('generate',1,source='ironclad_pool',filter='skill',selection='random',cost_free=True,scope='turn',destination='hand')]
    if n=='DualWield':return [step('clone',val('Cards'),source='hand',filter='attack_or_power',selection='chosen',destination='hand',reference='chosen')]
    if n=='Enlightenment':return [modify('cost',1,operator='clamp',target='all_matching_cards',source='hand',maximum=1,scope='combat'if u else'until_played')]
    if n=='Entrench':return [step('block',[state('block')],target='self',unpowered=True)]
    if n=='Exterminate':return [d(count=val('Repeat'),target='all_enemies')]
    if n=='FeedingFrenzy':return power_program('SetupStrike',val('Strength'))
    if n=='HelloWorld':return power_program(n,1)
    if n=='MadScience':return [step('none')]  # Instance-specific program required below.
    if n=='Maul':return [d(count=2),modify('damage',val('Increase'),target='all_matching_cards',source='all_piles',filter='same_definition',reference='self',scope='combat')]
    if n=='Metamorphosis':return [step('generate',val('Cards'),source='ironclad_pool',filter='attack',with_replacement=True,destination='draw_random',cost_free=True,scope='combat')]
    if n=='NeowsFury':return [d(),move('discard','hand',count=val('Cards'),can_skip=True,minimum=0)]
    if n=='Outmaneuver':return power_program('NextTurnEnergy',val('Energy'))
    if n=='Peck':return [d(count=val('Repeat'))]
    if n=='Rebound':return [d()]+power_program(n,1)
    if n=='Relax':return [b()]+power_program('NextTurnDraw',val('Cards'))+power_program('NextTurnEnergy',val('Energy'))
    if n=='RipAndTear':return [d(count=2,target='random_enemy',stochastic=True)]
    if n=='Squash':return [d(),status('vulnerable',val('Vulnerable'),'enemy')]
    if n=='Stack':return [block(expression('add',constant(val('CalculationBase')),state('discard_count')))]
    if n=='ToricToughness':return [b()]+power_program(n,val('Turns'),[state('source_block',snapshot=1)])
    if n=='Wish':return [move('draw','hand')]
    if n=='Whistle':return [d(),status('stunned',1,'enemy',scope='turn')]
    if n in ('ByrdonisEgg','Dowsing','LanternKey','SpoilsMap'):return [step('none')]
    # Neutral generated cards and curses/statuses. Passive-only cards deliberately
    # have a common no-op descriptor: metadata/keywords still distinguish them.
    if n in ('AscendersBane','Clumsy','CurseOfTheBell','Dazed','Debris','Folly','Greed','Guilty','Injury','PoorSleep','Soot','SporeMind','Wound','Writhe'):return [step('none')]
    if n in ('BadLuck','Beckon'):return rule('turn_end',[step('lose_hp',val('HpLoss'),target='self',unpowered=True)],condition='is_in_hand')
    if n in ('Burn','Decay','Infection','Toxic','Wither'):return rule('turn_end',[step('damage',val('Damage'),target='self',unpowered=True)],condition='is_in_hand')
    if n=='Debt':return rule('turn_end',[step('gold',-val('Gold'),target='self')],condition='is_in_hand')
    if n in ('Disintegration','MindRot','Sloth','WasteAway'):return power_program(n,val(n))
    if n in ('Doubt','Shame'):return rule('turn_end',[status('weak'if n=='Doubt'else'frail',1,skip_first=True)],condition='is_in_hand')
    if n=='Enthralled':return [modify('unplayable',1,operator='prevent',filter='not_source',condition='is_in_hand')]
    if n=='FranticEscape':return [modify('escape_progress',1,target='enemy'),modify('cost',1,target='source_card',scope='combat')]
    if n=='Normality':return [modify('unplayable',1,operator='prevent',condition='is_in_hand',terms=expression('greater_equal',state('cards_played_turn'),constant(3)))]
    if n=='Regret':return rule('turn_end',[step('lose_hp',[state('hand_count',snapshot=1)],target='self',unpowered=True)],condition='is_in_hand',snapshot=True)
    if n=='Slimed':return [dr()]
    if n=='Void':return rule('self_draw',[step('energy',-val('Energy'),target='self')])
    raise UnsupportedSemantics(f'no reviewed card program: {n}')

@dataclass(frozen=True)
class Descriptor:
    steps:tuple
    evidence:tuple=()

class Catalog:
    def __init__(self,metadata=None):
        data=metadata or json.loads(Path(__file__).with_name('source_metadata.json').read_text())
        self.source_version=data['source_version'];self.cards={r['engine_id']:r for r in data['cards']};self.by_name={r['name']:r for r in data['cards']};self.by_entry={r['entry']:r for r in data['cards']}
        self.descriptors=[Descriptor(tuple())];self.card_descriptors={};self.effect_descriptors={};self.excluded={};self._arrays={}
        for r in data['cards']:
            if r['name']=='Splash':self.excluded[r['engine_id']]='foreign-character generation';continue
            for u in (False,True):
                program=card_program(r,u);self.card_descriptors[r['engine_id'],u]=self._add(program,[r['source']])
        power_names=set(p for p,k in POWER_CARD_KEYS.values())|{'Thorns','Intangible','HelloWorld','NextTurnEnergy','NextTurnDraw','Rebound','ToricToughness','Curious','Improvement','Strangle','TemporaryStrength','Strength','Dexterity','Vulnerable','Weak','Frail','Vigor','BlockNextTurn','Colossus','DarkShackles','Mangle','SetupStrike','FlameBarrier','FreeAttack','NoBlock','NoDraw','OneTwoPunch','Rage','RetainHand','TheBomb','TheGambit','Disintegration','MindRot','Sloth','WasteAway'}
        for n in sorted(power_names):self.effect_descriptors[n]=self._add(power_program(n),[f'MegaCrit.Sts2.Core.Models.Powers/{n}Power.cs'])
    def _add(self,program,evidence):
        for s in program:
            unknown=set(s)-set(CATEGORIES)-set(STEP_NUMERIC)-set(STEP_FLAGS)-{'terms'}
            if unknown:raise ValueError(f'unencoded semantic fields {unknown}: {s}')
            encode_step(s)
            for t in s.get('terms',[]):encode_term(t)
        self.descriptors.append(Descriptor(tuple(program),tuple(evidence)));return len(self.descriptors)-1
    def card(self,engine_id,upgraded=False):
        try:return self.card_descriptors[int(engine_id),bool(upgraded)]
        except KeyError:raise UnsupportedSemantics(f'unsupported card {engine_id}: {self.excluded.get(engine_id,"not in reviewed solo scope")}')from None
    def effect(self,name):
        n=POWER_ALIASES.get(name,name.removesuffix('Power'));return self.effect_descriptors.get(n,0)
    def descriptor_arrays(self,indices=None):
        indices=tuple(range(len(self.descriptors)))if indices is None else tuple(int(i)for i in indices)
        rows=[self.descriptors[i]for i in indices];S=max(1,max((len(d.steps)for d in rows),default=0));T=max(1,max((len(s.get('terms',[]))for d in rows for s in d.steps),default=0));J=len(rows)
        steps=np.zeros((J,S,len(STEP_FIELDS)),np.float32);sm=np.zeros((J,S),bool);terms=np.zeros((J,S,T,len(TERM_FIELDS)),np.float32);tm=np.zeros((J,S,T),bool)
        for j,d in enumerate(rows):
            for si,s in enumerate(d.steps):
                steps[j,si]=encode_step(s);sm[j,si]=True
                for ti,t in enumerate(s.get('terms',[])):terms[j,si,ti]=encode_term(t);tm[j,si,ti]=True
        return dict(semantic_steps=steps,semantic_step_mask=sm,semantic_terms=terms,semantic_term_mask=tm)
    def describe(self):return dict(schema='semantic_card.catalog/v1',source_version=self.source_version,cards=len(self.card_descriptors)//2,pool_cards=sum(r['scope']!='neutral'and r['name']!='Splash'for r in self.cards.values()),neutral_cards=sum(r['scope']=='neutral'for r in self.cards.values()),excluded={self.cards[k]['name']:v for k,v in self.excluded.items()},active_rules=len(self.effect_descriptors),descriptors=len(self.descriptors),identity_policy='lookup IDs only; shared mechanics tensors have no card or source identity')

@lru_cache(maxsize=1)
def default_catalog():return Catalog()


def enchantment_program(name,target='enemy'):
    a=[state('enchant_amount')]
    if name in ('None','Clone'):return []
    if name=='Sharp':return [modify('damage',a,powered=True,target='source_card')]
    if name=='Nimble':return [modify('block',a,powered=True,target='source_card')]
    if name=='Swift':return rule('play',[draw(a)],limit=1,scope='combat')
    if name=='Steady':return [modify('retain',1,operator='set',target='source_card')]
    if name=='Spiral':return [modify('play_count',1,target='source_card')]
    if name=='Sown':return rule('play',[step('energy',a,target='self')],limit=1,scope='combat')
    if name=='Corrupted':return [modify('damage',1.5,operator='multiply',powered=True,target='source_card')]+rule('play',[step('lose_hp',2,target='self',unpowered=True)])
    if name=='Slither':return rule('self_draw',[modify('cost',None,operator='set',target='source_card',minimum=0,maximum=3,stochastic=True,scope='combat')])
    if name=='Vigorous':return [modify('damage',a,powered=True,target='source_card',scope='until_played')]
    if name=='Imbued':return rule('combat_start',[step('autoplay',target='source_card',source='current_card',cost_free=True)],phase='after_draw',limit=1)
    if name=='Goopy':return [modify('destination',None,operator='set',target='source_card',destination='exhaust'),modify('block',expression('subtract',state('enchant_amount'),constant(1)),target='source_card')]+rule('play',[modify('block',1,target='source_card',scope='permanent')])
    if name=='TezcatarasEmber':return [modify('cost',0,operator='set',target='source_card',scope='permanent'),modify('damage',3,target='source_card',powered=True)]
    if name=='Inky':return rule('play',[status('weak',1,target)])
    if name=='Adroit':return rule('play',[block(a)])
    if name=='Momentum':return rule('play',[modify('damage',a,target='source_card',scope='combat')])
    if name=='RoyallyApproved':return [modify('retain',1,operator='set',target='source_card'),modify('innate',1,operator='set',target='source_card')]
    if name=='PerfectFit':return rule('shuffle',[move('current_card','draw_top',selection='all')],skip_first=True)
    if name=='SoulsPower':return [modify('exhaust_permission',0,operator='prevent',target='source_card')]
    if name=='Instinct':return [modify('damage',2,operator='multiply',target='source_card',powered=True)]
    if name=='Glam':return [modify('play_count',1,target='source_card',scope='until_played',limit=1)]
    raise UnsupportedSemantics(f'unreviewed enchantment {name}')

# A composed descriptor still contains only shared mechanics; its dispatch key is
# never supplied to the network. Composition is cached once per observed overlay.
def _card_with_enchantment(self,engine_id,upgraded,enchantment='None'):
    base=self.card(engine_id,upgraded)
    if enchantment in ('None','Clone'):return base
    key=(int(engine_id),bool(upgraded),enchantment)
    if not hasattr(self,'_composed'):self._composed={}
    if key not in self._composed:
        row=self.cards[int(engine_id)];target='all_enemies'if row['target']=='AllEnemies'else'enemy'
        self._composed[key]=self._add(list(self.descriptors[base].steps)+enchantment_program(enchantment,target),self.descriptors[base].evidence+(f'MegaCrit.Sts2.Core.Models.Enchantments/{enchantment}.cs',))
    return self._composed[key]
Catalog.card_with_enchantment=_card_with_enchantment


def mad_science_program(card_type,rider):
    if card_type not in ('Attack','Skill','Power'):raise UnsupportedSemantics(f'MadScience missing valid public card type {card_type}')
    if rider not in ('None','Sapping','Violence','Choking','Energized','Wisdom','Chaos','Expertise','Curious','Improvement'):raise UnsupportedSemantics(f'MadScience missing valid public rider {rider}')
    if card_type=='Attack':ops=[damage(12,count=3 if rider=='Violence'else 1)]
    elif card_type=='Skill':ops=[block(8)]
    else:
        ops=[status('strength',2),status('dexterity',2)]if rider=='Expertise'else power_program('Curious',1)if rider=='Curious'else power_program('Improvement',1)if rider=='Improvement'else[step('none')]
    if rider=='Sapping':ops+=[status('weak',2,'enemy'),status('vulnerable',2,'enemy')]
    elif rider=='Choking':ops+=power_program('Strangle',6)
    elif rider=='Energized':ops+=[step('energy',2,target='self')]
    elif rider=='Wisdom':ops+=[draw(3)]
    elif rider=='Chaos':ops+=[step('generate',1,source='ironclad_pool',destination='hand',cost_free=True,scope='turn',stochastic=True)]
    return ops


def _instance_descriptor(self,engine_id,upgraded,enchantment='None',tinker_type=None,tinker_rider=None):
    if self.cards.get(engine_id,{}).get('name')!='MadScience':return self.card_with_enchantment(engine_id,upgraded,enchantment)
    if not hasattr(self,'_composed'):self._composed={}
    key=(int(engine_id),bool(upgraded),enchantment,tinker_type,tinker_rider)
    if key not in self._composed:
        ops=mad_science_program(tinker_type,tinker_rider)+enchantment_program(enchantment)
        self._composed[key]=self._add(ops,[self.cards[engine_id]['source']])
    return self._composed[key]
Catalog.instance_descriptor=_instance_descriptor
