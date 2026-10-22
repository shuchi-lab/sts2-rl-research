"""Pure Act3 checkpoint provenance reader; no inference, hashes, contexts or replay."""
STATE_KEY='bec1_state'
FT_STATE_SCHEMA='battle-act3-ft50k/learner-state/v1'
INITIALIZATION='fresh_fine_tune'
CONTRACT='all-boss-free'
REWARD_ID='combat-potion-turn100-boss-act-v2-victory-bill-all-boss-free-v1'

def finetune_problems(model, blob:dict, meta:dict, reward_id:str)->list[str]:
    mi,bm=blob.get('model_identity') or {},blob.get('meta') or {}
    st=blob.get(STATE_KEY) or {}
    counters,sc=bm.get('counters') or {},st.get('counters') or {}
    cfg,si=st.get('config') or {},st.get('source_identity') or {}
    rc=bm.get('reward_contract') or {}
    n=meta['additional_fights'];prior=meta['prior_act1_finetune_fights'];prior2=meta['prior_act2_finetune_fights']
    checks=[
        ('model_identity',mi,meta['model_identity']),('meta.profile',bm.get('profile'),meta['profile']),
        ('meta.model',bm.get('model'),mi),('parameter_count',model.parameter_count(),meta['parameters']),
        ('meta.stage',bm.get('stage'),2),('meta.boundary',bm.get('boundary'),'drained'),
        ('meta.original_stage1',bm.get('original_stage1_fights'),meta['original_stage1_fights']),
        ('meta.original_stage2',bm.get('original_stage2_fights'),meta['original_stage2_fights']),
        ('meta.prior_act1',bm.get('prior_act1_finetune_fights'),prior),
        ('meta.prior_act2',bm.get('prior_act2_finetune_fights'),prior2),
        ('meta.source_cumulative',bm.get('source_cumulative_online_fights'),meta['source_cumulative_online_fights']),
        ('meta.additional_act3',bm.get('additional_fights'),n),
        ('meta.cumulative',bm.get('cumulative_online_fights'),meta['total_online_fights']),
        ('counters.stage1',counters.get('stage1_online'),meta['original_stage1_fights']),
        ('counters.stage2',counters.get('stage2_online'),meta['original_stage2_fights']),
        ('counters.act1',counters.get('act1_finetune_online'),prior),
        ('counters.act2',counters.get('act2_finetune_online'),prior2),
        ('counters.act3',counters.get('act3_finetune_online'),n),
        ('counters.cumulative',sum(counters.get(k,0) for k in ('stage1_online','stage2_online','act1_finetune_online','act2_finetune_online','act3_finetune_online')),meta['total_online_fights']),
        ('meta.reward_id',bm.get('reward_id'),reward_id),('reward_all_boss_free',bm.get('reward_id'),REWARD_ID),
        ('reward_contract.training',rc.get('training_reward'),reward_id),
        ('reward_contract.bill',str(rc.get('potion_bill','')).split(':')[0],CONTRACT),
        ('state.schema',st.get('schema'),FT_STATE_SCHEMA),('state.stage',st.get('stage'),2),
        ('state.boundary',st.get('boundary'),'drained'),('state.in_flight',st.get('in_flight'),0),
        ('state.dispatched',st.get('dispatched'),n),('state.completed',st.get('completed'),n),
        ('state.counters',sc,counters),('state.config.seed',cfg.get('seed'),meta['seed']),
        ('state.config.profile',cfg.get('profile'),meta['profile']),
        ('state.source_checkpoint',st.get('source_checkpoint'),meta['source_identity']['checkpoint_windows']),
        ('config.source_checkpoint',cfg.get('source_checkpoint'),meta['source_identity']['checkpoint_windows']),
        ('state.initialization',st.get('initialization'),meta['initialization']),
        ('source_identity',si,meta['source_identity']),
    ]
    return [f'{key}: {got!r} != {want!r}' for key,got,want in checks if got!=want]

