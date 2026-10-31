using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace STS2_MCP;

public static partial class McpMod
{
    private static int _battleSelectionSelectedCount;
    private static CardModel[] _battleSelectionToHand = Array.Empty<CardModel>();
    private static CardModel[] _battleSelectionDiscarded = Array.Empty<CardModel>();
    private static readonly Dictionary<string,string> BattleSelectionSources = new(StringComparer.Ordinal) {
        ["Headbutt"] = "DiscardToDrawPileTop", ["TrueGrit"] = "ExhaustFromHand",
        ["NeowsFury"] = "DiscardToHand", ["Brand"] = "ExhaustFromHand",
        ["SeekerStrike"] = "DrawPileToHand", ["Wish"] = "DrawPileToHand",
        ["Purity"] = "ExhaustFromHandRepeated",
        ["BurningPact"] = "ExhaustFromHandThenDraw", ["Warcry"] = "HandToDrawPileTop",
        ["Armaments"] = "UpgradeInHand", ["DualWield"] = "DualWield",
        ["SecretTechnique"] = "DrawPileToHand", ["SecretWeapon"] = "DrawPileToHand",
        ["ThinkingAhead"] = "HandToDrawPileTop", ["Forethought"] = "HandToDrawPileTop",
        ["Exhume"] = "DiscardToHand", ["Prepared"] = "DiscardFromHandRepeated",
        ["DaggerThrow"] = "DiscardFromHandRepeated", ["Survivor"] = "DiscardFromHandRepeated",
        ["GamblingChip"] = "DiscardAnyThenDraw",
        ["GamblersBrew"] = "DiscardAnyThenDraw", ["LiquidMemories"] = "DiscardToHand",
        ["DropletOfPrecognition"] = "DrawPileToHand",
        ["AttackPotion"] = "GeneratedCardToHand", ["SkillPotion"] = "GeneratedCardToHand",
        ["PowerPotion"] = "GeneratedCardToHand", ["ColorlessPotion"] = "GeneratedCardToHand",
        ["ChoicesParadox"] = "GeneratedCardToHand", ["Discovery"] = "GeneratedCardToHand",
        ["MadScience"] = "GeneratedCardToHand", ["HandTrick"] = "MarkHandCardSly",
        ["WellLaidPlansPower"] = "RetainForNextTurn", ["ToolsOfTheTradePower"] = "DiscardFromHandRepeated",
        ["Acrobatics"] = "DiscardFromHandRepeated", ["DyingStar"] = "ExhaustFromHandRepeated",
        ["Scavenge"] = "ExhaustFromHand", ["Setup"] = "HandToDrawPileTop",
        ["Transfigure"] = "TransfigureInHand", ["Transmogrify"] = "TransformDrawPileToSoul",
        ["TouchOfInsanity"] = "FreeThisCombatInHand", ["Ashwater"] = "ExhaustAnyFromHand",
        ["Recycle"] = "ExhaustFromHand", ["Skim"] = "DiscardFromHandRepeated",
        ["MasterOfStrategy"] = "GeneratedCardToHand"
    };
    private static CardModel[] BattleGridCards(NCardGridSelectionScreen screen,BattleSelector trace,Player player,
        out CardPile? pile,out Func<CardModel,bool>? filter)
    {
        pile=null; filter=null;
        if(screen is not NCombatPileCardSelectScreen)
        {
            if(trace.Pile!=null) throw new BattleProjectionUnsupported("Captured combat pile has a different selector screen");
            return ((IEnumerable)BattleRead(screen,"_cards")!).Cast<CardModel>().ToArray();
        }
        // This subclass deliberately leaves inherited _cards empty. Its current
        // legal models come from the live pile and filter, including virtualized
        // cards that do not have an on-screen holder.
        pile=(CardPile)BattleRead(screen,"_pile")!;
        filter=(Func<CardModel,bool>?)BattleRead(screen,"_filter");
        var prefs=(MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs)BattleRead(screen,"_prefs")!;
        if(trace.Method!="FromCombatPile" || trace.Pile==null || !ReferenceEquals(trace.Pile,pile) ||
            !ReferenceEquals(trace.Filter,filter) || !ReferenceEquals(trace.Player,player) || trace.Prefs==null ||
            trace.Prefs.Value.MinSelect!=prefs.MinSelect || trace.Prefs.Value.MaxSelect!=prefs.MaxSelect ||
            trace.Prefs.Value.RequireManualConfirmation!=prefs.RequireManualConfirmation ||
            trace.Prefs.Value.Cancelable!=prefs.Cancelable)
            throw new BattleProjectionUnsupported("Combat pile selector does not match captured native source/prefs");
        var cards=pile.Cards.ToArray();
        if(cards.Any(card=>!ReferenceEquals(card.Owner,player)))
            throw new BattleProjectionUnsupported("Combat pile selection contains cards from a different player");
        if(trace.Source?.GetType().Name is "LiquidMemories" or "DropletOfPrecognition")
        {
            var potion=(PotionModel)trace.Source;
            string name=potion.GetType().Name;
            if(name=="LiquidMemories")
                BattlePileSelection.RequireLiquidMemories(name,potion.Id.Entry,AgentIds.Id("potions",potion.Id.Entry),
                    pile.Type.ToString(),prefs.MinSelect,prefs.MaxSelect,
                    prefs.RequireManualConfirmation,prefs.Cancelable);
            else
                BattlePileSelection.RequireDropletOfPrecognition(name,potion.Id.Entry,AgentIds.Id("potions",potion.Id.Entry),
                    pile.Type.ToString(),prefs.MinSelect,prefs.MaxSelect,
                    prefs.RequireManualConfirmation,prefs.Cancelable);
            if(AgentNativeChoiceAction(trace.ChoiceContext) is not UsePotionAction owningAction ||
                !ReferenceEquals(owningAction.Player,player))
                throw new BattleProjectionUnsupported(name+" selector is not owned by the local native UsePotionAction");
            var currentFilter=filter;
            if(currentFilter!=null && cards.Any(card=>!currentFilter(card)))
                throw new BattleProjectionUnsupported(name+" must offer the complete combat pile");
        }
        return cards;
    }
    private static void BattleNestedAutoContinuation(Player player,CardPlay[] pending,AgentDecision answer)
    {
        var trace = _battleSelection ?? throw new BattleProjectionUnsupported("Selection origin was not captured");
        if (NOverlayStack.Instance?.Peek() is not NCombatPileCardSelectScreen grid || NCombatRoom.Instance?.Ui?.Hand?.IsInCardSelection == true)
            throw new BattleProjectionUnsupported("Selection interrupted an automatic play outside a combat pile selector; nested/batch continuation context is not certified");
        var run = player.RunState as MegaCrit.Sts2.Core.Runs.RunState ?? throw new BattleProjectionUnsupported("Run state unavailable");
        var cards = BattleGridCards(grid,trace,player,out var pile,out var filter);
        var eligible = cards.Where(c => filter == null || filter(c)).ToArray();
        int pick = BattleNestedAutoSelection.FrozenPick(pending.Select(p => (p.Card.GetType().Name,p.IsAutoPlay)).ToArray(),
            trace.Source?.GetType().Name ?? "",ReferenceEquals(trace.Source,pending[^1].Card),trace.Method,
            pile!.Type.ToString(),eligible.Length,trace.Prefs?.MaxSelect ?? 1);
        var card = eligible[pick];
        var selected = ((IEnumerable)BattleRead(grid,"_selectedCards")!).Cast<CardModel>().ToArray();
        bool preview = (bool)BuildCardSelectState(grid,run)["preview_showing"]!;
        answer.Owner = "automatic"; answer.Phase = "Confirmation";
        if (selected.Length == 1 && ReferenceEquals(selected[0],card))
        {
            answer.Candidates.Add(new AgentCandidate { Id="battle-selection-confirm",Kind="confirm_selection",Label="Confirm selected card",
                Action=new() { ["action"]=BattleProjectionNames.SelectionConfirmAction(false) },Execute=() => ExecuteConfirmSelection() });
            return;
        }
        if (selected.Length != 0 || preview)
            throw new BattleProjectionUnsupported("Nested automatic selection already holds a pick other than the frozen one");
        answer.Candidates.Add(new AgentCandidate { Id="battle-nested-auto-select",Kind="select",Label=card.Title,
            Action=new() { ["action"]="select_card",["card_handle"]=AgentObjectIds.Card(card) },Execute=() => {
                if (_battleSelection != trace || trace.Task.IsCompleted) return Error("Selection binding is stale");
                if (NOverlayStack.Instance?.Peek() is not NCardGridSelectionScreen current || !ReferenceEquals(current,grid))
                    return Error("Bound selection screen changed");
                var currentSelected = ((IEnumerable)BattleRead(current,"_selectedCards")!).Cast<CardModel>().ToArray();
                try
                {
                    var currentCards = BattleGridCards(current,trace,player,out var currentPile,out var currentFilter);
                    BattlePileSelection.Validate(trace.Pile!,currentPile!,currentCards,currentFilter,currentSelected,card);
                }
                catch (BattleProjectionUnsupported e) { return Error(e.Message); }
                current.GetType().GetMethod("OnCardClicked",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(current,new object[] {card});
                // The frozen pick is a single card; settle the native screen here.
                if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen after && ReferenceEquals(after,current) &&
                    (bool)BuildCardSelectState(after,run)["can_confirm"]!) return ExecuteConfirmSelection();
                return new() { ["status"]="ok" };
            }});
    }
    private static Dictionary<string,object?>? BattleSelectionProjection(Player player,CombatState combat,AgentDecision answer)
    {
        var trace = _battleSelection ?? throw new BattleProjectionUnsupported("Selection origin was not captured");
        int boundDecision=_battleDecision;
        string sourceType = trace.Source?.GetType().Name ?? "";
        if (!BattleSelectionSources.TryGetValue(sourceType,out string? kind))
        {
            if (trace.Method == "FromChooseACardScreen") kind = "GeneratedCardToHand";
            else throw new BattleProjectionUnsupported("Selection kind lacks reviewed source mapping: " + sourceType + "/" + trace.Method);
        }
        var overlay = NOverlayStack.Instance?.Peek();
        var hand = NCombatRoom.Instance?.Ui?.Hand;
        var prefs = trace.Prefs;
        int maximum = prefs?.MaxSelect ?? 1;
        int sourcePotion = trace.Source is PotionModel sp ? AgentIds.Id("potions",sp.Id.Entry) : 0;
        bool returnsToHand = BattleSelectionFlow.ReturnsToHand(kind,sourcePotion);
        bool skippable = (prefs?.MinSelect ?? 1) == 0 || trace.CanSkip;
        _battleSelectionSelectedCount = 0;
        var selectedCards = Array.Empty<CardModel>();
        var holders = new List<(CardModel Card,Control? Holder,int Index)>();
        bool handSelection = hand?.IsInCardSelection == true;
        bool preview = false;
        if (handSelection)
        {
            var filter = (Func<CardModel,bool>?)BattleRead(hand!,"_currentSelectionFilter");
            // Native _selectedCards is an ordered List; keep the pick order DiscardAndDraw uses.
            selectedCards = ((IEnumerable)BattleRead(hand!,"_selectedCards")!).Cast<CardModel>().ToArray();
            var selected = selectedCards.ToHashSet();
            _battleSelectionSelectedCount = selected.Count;
            int i = 0;
            foreach (var h in hand!.ActiveHolders)
            {
                if (h.CardModel != null && !selected.Contains(h.CardModel) && (filter == null || filter(h.CardModel))) holders.Add((h.CardModel,h,i));
                i++;
            }
        }
        else if (overlay is NCardGridSelectionScreen grid)
        {
            var uiState = BuildCardSelectState(grid,player.RunState as MegaCrit.Sts2.Core.Runs.RunState ?? throw new BattleProjectionUnsupported("Run state unavailable"));
            preview = (bool)uiState["preview_showing"]!;
            var selected=((IEnumerable)BattleRead(grid,"_selectedCards")!).Cast<CardModel>().ToArray();
            selectedCards = selected;
            _battleSelectionSelectedCount=selected.Length;
            var cards=BattleGridCards(grid,trace,player,out var pile,out var filter);
            var anchors=FindAllSortedByPosition<NGridCardHolder>(grid).Where(h=>h.CardModel!=null)
                .Select(h=>(h.CardModel!,(Control)h));
            var offered=pile==null ? BattleGridSurface.Candidates(cards,selected,anchors) :
                BattlePileSelection.Candidates(trace.Pile!,pile,cards,filter,selected,anchors);
            for(int i=0;i<offered.Count;i++) holders.Add((offered[i].Card,offered[i].Anchor,i));
        }
        else if (overlay is NChooseACardSelectionScreen choose)
        {
            var grids = FindAllSortedByPosition<NGridCardHolder>(choose);
            for (int i=0;i<grids.Count;i++) if (grids[i].CardModel != null) holders.Add((grids[i].CardModel!,grids[i],i));
        }
        else throw new BattleProjectionUnsupported("Selection has no supported native control");
        bool discardsEachPick = handSelection && BattleSelectionFlow.DiscardsEachPick(kind,sourcePotion);
        if (handSelection && (BattleSelectionFlow.StagesHandCards(kind,sourcePotion) || discardsEachPick))
        {
            var handCards = player.PlayerCombatState!.Hand.Cards.ToList();
            holders = holders.OrderBy(h => handCards.IndexOf(h.Card)).ToList();
        }
        if (maximum > 1 && kind is not "GeneratedCardToHand" && !returnsToHand && !BattleSelectionFlow.StagesHandCards(kind,sourcePotion) && !discardsEachPick)
            throw new BattleProjectionUnsupported("Deferred multi-card selector needs a certified sequential projection: " + sourceType);
        if (preview || (!BattleSelectionFlow.StagesHandCards(kind,sourcePotion) && BattleSelectionFlow.Finished(maximum,_battleSelectionSelectedCount,holders.Count)))
        {
            answer.Owner = "automatic"; answer.Phase = "Confirmation";
            answer.Candidates.Add(new AgentCandidate { Id="battle-selection-confirm",Kind="confirm_selection",Label="Confirm selected card",Action=new() { ["action"]=BattleProjectionNames.SelectionConfirmAction(handSelection) },
                Execute=() => handSelection ? ExecuteCombatConfirmSelection() : ExecuteConfirmSelection() });
            return null;
        }
        int sourceCard = trace.Source is CardModel sc && kind != "DualWield" ? AgentIds.Id("cards",sc.Id.Entry) : 0;
        int amount = BattleSelectionFlow.Amount(sourceType,kind,(trace.Source as CardModel)?.IsUpgraded==true,
            maximum,_battleSelectionSelectedCount);
        if(returnsToHand) _battleSelectionToHand = selectedCards;
        if(discardsEachPick && selectedCards.Length > 0)
        {
            // Frozen per-pick discards already ran these; their native counterparts run only at confirm.
            if(player.Relics.Any(r => r.GetType().Name is "ToughBandages" or "Tingsha"))
                throw new BattleProjectionUnsupported("Staged Gambling Chip discards would already have triggered frozen AfterCardDiscarded relic effects");
            if(selectedCards.Any(c => c.IsSlyThisTurn))
                throw new BattleProjectionUnsupported("Staged Gambling Chip Sly discard would already be in the frozen autoplay queue");
            _battleSelectionDiscarded = selectedCards;
        }
        var projected=BattleSelectionRows.Canonicalize(holders,
            h=>h.Card.Pile?.Type==PileType.Draw,h=>BattleCardRow(h.Card,combat),h=>AgentObjectIds.Card(h.Card));
        var selection = new Dictionary<string,object?> { ["kind"] = AgentIds.Id("selection_kinds",kind),["kind_name"] = kind,
            ["amount"] = amount,["selected_count"] = BattleSelectionFlow.SelectedCount(kind,sourcePotion,handSelection,_battleSelectionSelectedCount),["skippable"] = skippable,
            ["source_card_def_id"] = sourceCard,["source_potion_id"] = sourcePotion,["candidate_count"] = holders.Count,
            ["generated_upgraded"] = kind=="GeneratedCardToHand" && holders.Count > 0 && holders.All(h => h.Card.IsUpgraded),
            ["generated_retained"] = kind=="GeneratedCardToHand" && holders.Count > 0 && holders.All(h => h.Card.ShouldRetainThisTurn),
            ["after_selection_to_hand"] = Array.Empty<double[]>() };
        for (int ordinal=0;ordinal<projected.Count;ordinal++)
        {
            var (card,holder,_) = projected[ordinal].Source;
            string pile = card.Pile?.Type.ToString().ToLowerInvariant() switch {"hand"=>"hand","draw"=>"draw","discard"=>"discard",_=>"generated"};
            int action = ordinal;
            var row = new Dictionary<string,object?> { ["o"] = answer.Candidates.Count,["kind"]="select",["action"] = action,["target"]=-1,
                ["pile"] = pile,["card"] = projected[ordinal].Features };
            if (pile is "hand" or "discard") row["pile_index"] = card.Pile!.Cards
                .Where(c=>!(returnsToHand || discardsEachPick) || !selectedCards.Contains(c)).ToList().IndexOf(card);
            string candidateId=BattleProjectionNames.OptionId("select",action,-1,null);
            answer.Candidates.Add(new AgentCandidate {Id=candidateId,Kind="select",Label=card.Title,
                Public=row,Anchor=holder,Action=new() {["action"]=handSelection ? "combat_select_card" : "select_card",["card_handle"]=AgentObjectIds.Card(card)},
                Execute=() => {
                    if (_battleSelection != trace || trace.Task.IsCompleted) return Error("Selection binding is stale");
                    if (handSelection)
                    {
                        var current = hand!.ActiveHolders.ToList(); int index=current.FindIndex(h => h.CardModel == card);
                        if (index < 0) return Error("Bound hand-selection card disappeared");
                        return BattleExecuteLogical(candidateId,boundDecision,()=>
                            ExecuteCombatSelectCard(new() {["card_index"]=JsonSerializer.SerializeToElement(index)}));
                    }
                    var currentOverlay=NOverlayStack.Instance?.Peek();
                    if (currentOverlay is NCardGridSelectionScreen currentGrid)
                    {
                        if(!ReferenceEquals(currentGrid,overlay)) return Error("Bound selection screen changed");
                        if ((bool)BuildCardSelectState(currentGrid,(MegaCrit.Sts2.Core.Runs.RunState)player.RunState)["preview_showing"]!) return Error("Selection preview already open");
                        var currentSelected=((IEnumerable)BattleRead(currentGrid,"_selectedCards")!).Cast<CardModel>().ToArray();
                        try
                        {
                            var currentCards=BattleGridCards(currentGrid,trace,player,out var currentPile,out var currentFilter);
                            if(currentPile==null) BattleGridSurface.Validate(currentCards,currentSelected,card);
                            else BattlePileSelection.Validate(trace.Pile!,currentPile,currentCards,currentFilter,currentSelected,card);
                        }
                        catch(BattleProjectionUnsupported e) { return Error(e.Message); }
                        // A native handler can throw after effects. Propagate that
                        // uncertainty; only the pure membership guards are refusals.
                        var gridResult=BattleExecuteLogical(candidateId,boundDecision,()=> {
                            currentGrid.GetType().GetMethod("OnCardClicked",BindingFlags.Instance|BindingFlags.NonPublic)!
                                .Invoke(currentGrid,new object[] {card});
                            return new(){["status"]="ok"};
                        });
                        // Keep a batch selector open for the next policy pick.
                        // Completion is a separate automatic continuation once
                        // the maximum/all candidates have been selected.
                        if(maximum<=1 && NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen afterGrid && ReferenceEquals(afterGrid,currentGrid) &&
                            (bool)BuildCardSelectState(afterGrid,(MegaCrit.Sts2.Core.Runs.RunState)player.RunState)["can_confirm"]!) ExecuteConfirmSelection();
                        return gridResult;
                    }
                    if(holder==null || !GodotObject.IsInstanceValid(holder) || !ReferenceEquals(currentOverlay,overlay)) return Error("Bound choose-card screen is stale");
                    if(currentOverlay is NChooseACardSelectionScreen choosing &&
                        !BattleChooseCardTiming.Ready(Time.GetTicksMsec(),(ulong)BattleRead(choosing,"_openedTicks")!))
                        return Error("Choose-card opening animation is not complete");
                    var currentHolders=FindAllSortedByPosition<NGridCardHolder>((Node)currentOverlay!);
                    int currentIndex=currentHolders.FindIndex(h => h.CardModel == card);
                    if(currentIndex<0) return Error("Bound selection card disappeared");
                    var result=BattleExecuteLogical(candidateId,boundDecision,()=>
                        ExecuteSelectCard(new(){["index"]=JsonSerializer.SerializeToElement(currentIndex)}));
                    if((string?)result.GetValueOrDefault("status")=="ok") {
                        if(NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen after &&
                           (bool)BuildCardSelectState(after,(MegaCrit.Sts2.Core.Runs.RunState)player.RunState)["can_confirm"]!) ExecuteConfirmSelection();
                    }
                    return result;
                }});
        }
        if(skippable)
        {
            int action=holders.Count;
            string candidateId=BattleProjectionNames.OptionId("skip_selection",action,-1,null);
            answer.Candidates.Add(new AgentCandidate {Id=candidateId,Kind="skip_selection",Label="Finish / skip selection",
                Public=new(){["o"]=answer.Candidates.Count,["kind"]="skip_selection",["action"]=action,["target"]=-1},
                Action=new(){["action"]=BattleSelectionFlow.FinishAction(handSelection,overlay is NChooseACardSelectionScreen)},Execute=() => {
                    return BattleExecuteLogical(candidateId,boundDecision,()=>
                        handSelection ? ExecuteCombatConfirmSelection() : overlay is NChooseACardSelectionScreen
                            ? ExecuteCancelSelection() : ExecuteConfirmSelection());
                }});
        }
        return selection;
    }
}
