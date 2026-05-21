"""Tests for events."""
from collections import Counter
import json
from pathlib import Path
import time

import pytest
from conftest import run_headless_jsonl


class TestNeowEvent:
    def test_neow_is_first_event(self, game):
        state = game.start(seed="ne1")
        assert state["decision"] == "event_choice"
        assert "Neow" in str(state.get("event_name", ""))
        assert state["can_leave"] is False

    def test_neow_cannot_be_force_left(self, game):
        state = game.start(seed="neow-no-force-leave")
        assert state["decision"] == "event_choice"

        result = game.act("leave_room")

        assert result["type"] == "error"
        assert "Cannot leave this event" in result["message"]

    def test_neow_options(self, game):
        state = game.start(seed="ne2")
        for opt in state["options"]:
            assert "title" in opt
            assert isinstance(opt["title"], str)
            assert "is_locked" in opt

    def test_neow_option_vars(self, game):
        state = game.start(seed="ne3")
        for opt in state["options"]:
            if opt.get("vars"):
                for k, v in opt["vars"].items():
                    assert isinstance(v, (int, float))

    def test_choose_neow(self, game):
        state = game.start(seed="ne4")
        opts = [o for o in state["options"] if not o.get("is_locked")]
        state = game.act("choose_option", option_index=opts[0]["index"])
        assert state.get("decision") is not None

    def test_neow_pomander_upgrades_selected_card_and_reaches_map(self, game):
        state = game.start(seed="codex-manual-20260514_171724")
        pomander = next(o for o in state["options"] if o["title"] == "Pomander")

        state = game.act("choose_option", option_index=pomander["index"])

        assert state["decision"] == "card_select"
        assert state["min_select"] == 1
        assert state["max_select"] == 1
        assert state["can_skip"] is False
        skipped = game.act("skip_select")
        assert skipped["type"] == "error"
        assert "cannot be skipped" in skipped["message"]
        bash = next(c for c in state["cards"] if c["name"] == "Bash")

        state = game.act("select_cards", indices=str(bash["index"]))

        deck_bash = next(c for c in state["player"]["deck"] if c["name"] == "Bash")
        assert deck_bash["upgraded"] is True
        assert state["decision"] == "map_select"

    def test_neow_relic_card_select_carries_source_option_context(self, game):
        state = game.start(character="Defect", ascension=5, seed="manual-functional-defect-a5-20260519")
        lead_paperweight = next(o for o in state["options"] if o["title"] == "Lead Paperweight")

        state = game.act("choose_option", option_index=lead_paperweight["index"])

        assert state["decision"] == "card_select"
        assert state["prompt"] == f"{lead_paperweight['title']}: {lead_paperweight['description']}"
        source = state["source_event_option"]
        assert source["title"] == lead_paperweight["title"]
        assert source["description"] == lead_paperweight["description"]

    def test_neow_relic_card_select_keeps_context_after_debug_player_update(self, game):
        state = game.start(character="Defect", ascension=5, seed="manual-functional-defect-a5-20260519")
        lead_paperweight = next(o for o in state["options"] if o["title"] == "Lead Paperweight")
        game.set_player(hp=5000, max_hp=5000, gold=999)

        state = game.act("choose_option", option_index=lead_paperweight["index"])

        assert state["decision"] == "card_select"
        assert state["prompt"] == f"{lead_paperweight['title']}: {lead_paperweight['description']}"
        source = state["source_event_option"]
        assert source["title"] == lead_paperweight["title"]
        assert source["description"] == lead_paperweight["description"]

    def test_neow_hefty_tablet_card_select_exports_engine_skip_affordance(self, game):
        state = game.start(character="Defect", seed="manual-functional-defect-20260519-2")
        hefty_tablet = next(o for o in state["options"] if o["title"] == "Hefty Tablet")

        state = game.act("choose_option", option_index=hefty_tablet["index"])

        assert state["decision"] == "card_select"
        assert state["min_select"] == 0
        assert state["max_select"] == 1
        assert state["can_skip"] is True

    def test_neow_optional_relic_card_select_skip_resumes_event_task(self, game):
        state = game.start(character="Regent", ascension=5, seed="real-natural-regent-a5-en")
        game.set_player(hp=999, max_hp=999, gold=999)
        lead_paperweight = next(o for o in state["options"] if o["title"] == "Lead Paperweight")

        state = game.act("choose_option", option_index=lead_paperweight["index"])

        assert state["decision"] == "card_select"
        assert state["can_skip"] is True

        state = game.act("skip_select")

        assert state["decision"] == "map_select"


class TestEventDescriptions:
    def test_no_ismultiplayer_tag(self, game):
        state = game.start(seed="ed1")
        for opt in state.get("options", []):
            d = opt.get("description") or ""
            assert "IsMultiplayer" not in d

    def test_zero_energy_icon_event_option_text_is_preserved(self, game):
        state = game.start(character="Regent", seed="tanx-crossbow-5")
        game.skip_neow(state)
        state = game.enter_room("event", event="TANX")

        crossbow = next(option for option in state["options"] if option["title"] == "Crossbow")

        assert "It costs 0[E] this turn." in crossbow["description"]
        assert "It costs this turn." not in crossbow["description"]

    def test_event_export_does_not_use_hardcoded_semantic_descriptions(self):
        source = Path(__file__).resolve().parents[1] / "src" / "Sts2Headless" / "RunSimulator.cs"
        text = source.read_text(encoding="utf-8")

        assert "NormalizeEventOptionDescription" not in text
        assert "Gain {Gold} Gold. Lose {HpLoss} HP." not in text
        assert "Add {cardText} to your Deck." not in text
        assert "Obtain the {relicText}" not in text


class TestFakeMerchantEvent:
    def test_fake_merchant_exposes_shop_inventory(self, game):
        state = game.start(seed="fake-merchant-shop")
        game.skip_neow(state)
        game.set_player(gold=150)

        state = game.enter_room("event", event="FAKE_MERCHANT")

        assert state["decision"] == "fake_merchant_shop"
        assert state["event_name"] == "The Merchant???"
        assert state.get("description") is None
        assert len(state["relics"]) == 6
        for relic in state["relics"]:
            assert relic["name"]
            assert relic["description"]
            assert relic["cost"] > 0
            assert relic["is_stocked"] is True
        assert state["can_leave"] is True

    def test_fake_merchant_buy_relic_uses_inventory_entry(self, game):
        state = game.start(seed="fake-merchant-buy")
        game.skip_neow(state)
        game.set_player(gold=150)
        state = game.enter_room("event", event="FAKE_MERCHANT")
        relic = next(item for item in state["relics"] if item["can_buy"])

        state = game.act("buy_relic", relic_index=relic["index"])

        assert state["decision"] == "fake_merchant_shop"
        assert state["player"]["gold"] == 150 - relic["cost"]
        assert any(owned["id"] == relic["id"] for owned in state["player"]["relics"])
        bought = next(item for item in state["relics"] if item["index"] == relic["index"])
        assert bought["is_stocked"] is False
        assert bought["can_buy"] is False

    def test_fake_merchant_proceed_returns_to_map(self, game):
        state = game.start(seed="fake-merchant-proceed")
        game.skip_neow(state)
        game.set_player(gold=150)
        state = game.enter_room("event", event="FAKE_MERCHANT")

        state = game.act("proceed")

        assert state["decision"] == "map_select"


class TestSlipperyBridge:
    def test_slippery_bridge_random_card_var_is_card_name(self, game):
        state = game.start(seed="bridge-vars")
        game.skip_neow(state)
        state = game.enter_room("event", event="SLIPPERY_BRIDGE")

        assert state["decision"] == "event_choice"
        overcome = next(o for o in state["options"] if o["title"] == "Overcome")
        random_card = overcome["vars"]["RandomCard"]

        assert isinstance(random_card, str)
        assert random_card
        assert random_card != "0"

    def test_slippery_bridge_random_card_matches_removed_card(self, game):
        state = game.start(seed="bridge-removal-var")
        game.skip_neow(state)
        game.set_player(deck=[
            "STRIKE_IRONCLAD",
            "STRIKE_IRONCLAD",
            "STRIKE_IRONCLAD",
            "STRIKE_IRONCLAD",
            "STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
            "BASH",
            "SETUP_STRIKE",
            "SWORD_BOOMERANG",
            "ARMAMENTS",
            "POMMEL_STRIKE",
            "BLOODLETTING",
        ])
        state = game.enter_room("event", event="SLIPPERY_BRIDGE")

        overcome = next(o for o in state["options"] if o["title"] == "Overcome")
        random_card = overcome["vars"]["RandomCard"]
        before = Counter(c["name"] for c in state["player"]["deck"])

        state = game.act("choose_option", option_index=overcome["index"])

        after = Counter(c["name"] for c in state["player"]["deck"])
        assert before[random_card] == after[random_card] + 1

    def test_slippery_bridge_hold_on_stays_in_event(self, game):
        state = game.start(seed="bridge-hold")
        game.skip_neow(state)
        state = game.enter_room("event", event="SLIPPERY_BRIDGE")
        hp_before = state["player"]["hp"]
        deck_size_before = state["player"]["deck_size"]

        state = game.act("choose_option", option_index=1)

        assert state["decision"] == "event_choice"
        assert state["event_name"] == "Slippery Bridge"
        assert state["player"]["hp"] == hp_before - 3
        assert state["player"]["deck_size"] == deck_size_before


class TestDenseVegetation:
    def test_trudge_on_exports_engine_vars_and_observed_effect(self, game):
        state = game.start(seed="dense-vegetation")
        game.skip_neow(state)
        state = game.enter_room("event", event="DENSE_VEGETATION")

        trudge = next(o for o in state["options"] if o["title"] == "Trudge On")

        assert trudge["vars"]["Gold"] > 0
        assert trudge["vars"]["HpLoss"] > 0
        assert f"Gain {trudge['vars']['Gold']} Gold." in trudge["description"]
        assert "Remove a card" not in trudge["description"]

        hp_before = state["player"]["hp"]
        gold_before = state["player"]["gold"]
        deck_size_before = state["player"]["deck_size"]

        state = game.act("choose_option", option_index=trudge["index"])

        assert state["player"]["hp"] == hp_before - trudge["vars"]["HpLoss"]
        assert state["player"]["gold"] == gold_before + trudge["vars"]["Gold"]
        assert state["player"]["deck_size"] == deck_size_before

    def test_rest_option_advances_to_fight_page(self, game):
        state = game.start(seed="dense-vegetation-rest")
        game.skip_neow(state)
        state = game.enter_room("event", event="DENSE_VEGETATION")

        rest = next(o for o in state["options"] if o["title"] == "Rest")
        state = game.act("choose_option", option_index=rest["index"])

        assert state["decision"] == "event_choice"
        assert state["event_name"] == "Dense Vegetation"
        assert [o["title"] for o in state["options"]] == ["Fight!"]

        state = game.act("choose_option", option_index=state["options"][0]["index"])

        assert state["decision"] == "combat_play"
        assert state["context"]["room_type"] == "Monster"
        assert state["enemies"]


class TestAmalgamator:
    def test_combine_defends_finishes_after_card_selection(self, game):
        state = game.start(seed="amalgamator-combine-defends")
        game.skip_neow(state)
        state = game.enter_room("event", event="AMALGAMATOR")

        combine = next(o for o in state["options"] if o["title"] == "Combine Defends")
        state = game.act("choose_option", option_index=combine["index"])

        assert state["decision"] == "card_select"
        assert state["min_select"] == 2
        assert state["max_select"] == 2

        state = game.act("select_cards", indices="0,1")

        assert state["decision"] == "map_select"
        assert any(card["name"] == "Ultimate Defend" for card in state["player"]["deck"])


class TestCrystalSphere:
    def test_choose_option_opens_headless_crystal_sphere_state(self, game):
        state = game.start(seed="crystal-sphere-headless")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="CRYSTAL_SPHERE")

        option = next(o for o in state["options"] if o["title"] == "Uncover Future")
        state = game.act("choose_option", option_index=option["index"])

        assert state["decision"] == "crystal_sphere"
        assert state["event_name"] == "Crystal Sphere"
        assert state["grid_width"] == 11
        assert state["grid_height"] == 11
        assert state["divinations_remaining"] == 3
        assert state["tool"] == "big"
        assert state["clickable_cells"]
        assert any(cell["is_hidden"] is False for cell in state["cells"])

    def test_crystal_sphere_clicks_can_finish_and_proceed(self, game):
        state = game.start(seed="crystal-sphere-clicks")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="CRYSTAL_SPHERE")

        option = next(o for o in state["options"] if o["title"] == "Uncover Future")
        state = game.act("choose_option", option_index=option["index"])
        state = game.act("crystal_sphere_set_tool", tool="small")

        for _ in range(5):
            if state["decision"] != "crystal_sphere" or state.get("can_proceed"):
                break
            cell = state["clickable_cells"][0]
            state = game.act("crystal_sphere_click_cell", x=cell["x"], y=cell["y"])

        assert state["decision"] == "crystal_sphere"
        assert state["can_proceed"] is True

        state = game.act("crystal_sphere_proceed")

        assert not (
            state["decision"] == "event_choice"
            and state.get("event_name") == "Crystal Sphere"
            and any(o["title"] == "Uncover Future" for o in state.get("options", []))
        )

    def test_crystal_sphere_card_reward_pending_does_not_block_proceed(self, game):
        state = game.start(character="Necrobinder", seed="crystal-sphere-card-reward-race")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="CRYSTAL_SPHERE")

        option = next(o for o in state["options"] if o["title"] == "Uncover Future")
        state = game.act("choose_option", option_index=option["index"])

        for x, y in [(5, 5), (3, 0), (5, 0)]:
            state = game.act("crystal_sphere_click_cell", x=x, y=y)

        assert state["decision"] == "crystal_sphere"
        assert state["can_proceed"] is True
        assert any(item["item_kind"] == "card_reward" for item in state["revealed_items"])

        time.sleep(0.5)
        state = game.act("crystal_sphere_proceed")

        assert state["type"] != "error"
        assert state["decision"] == "card_reward"

    def test_crystal_sphere_exports_partial_item_fragments(self, game):
        state = game.start(seed="crystal-partial-0")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="CRYSTAL_SPHERE")

        option = next(o for o in state["options"] if o["title"] == "Payment Plan")
        state = game.act("choose_option", option_index=option["index"])
        state = game.act("crystal_sphere_set_tool", tool="small")

        for x, y in [(3, 0), (4, 0), (5, 0)]:
            state = game.act("crystal_sphere_click_cell", x=x, y=y)

        partial_items = [
            item for item in state["visible_items"]
            if item["is_fully_revealed"] is False
        ]
        assert partial_items
        partial = partial_items[0]
        assert partial["item_kind"] == "card_reward"
        assert partial["card_rarity"] == "Uncommon"
        assert partial["reward_preview"]["category"] == "card_reward"
        assert partial["reward_preview"]["card_rarity"] == "Uncommon"
        assert partial["reward_preview"]["card_choices"] == 3
        assert partial["visible_cells"] == [{"x": 5, "y": 0}]
        assert partial["revealed_cells"] == 1
        assert partial["total_cells"] == 4

        revealed_indexes = {item["index"] for item in state["revealed_items"]}
        assert partial["index"] not in revealed_indexes

        gold = next(item for item in state["revealed_items"] if item["item_kind"] == "gold")
        assert gold["gold_amount"] == 10
        assert gold["reward_preview"] == {
            "category": "gold",
            "amount": 10,
            "size": "small",
        }

    def test_crystal_sphere_exports_all_visual_reward_variants(self, game):
        state = game.start(seed="crystal-all-0")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="CRYSTAL_SPHERE")

        option = next(o for o in state["options"] if o["title"] == "Payment Plan")
        state = game.act("choose_option", option_index=option["index"])

        for x, y in [(3, 3), (7, 3), (3, 7), (7, 7), (5, 5), (5, 1)]:
            state = game.act("crystal_sphere_click_cell", x=x, y=y)

        visible = state["visible_items"]
        kinds = {item["item_kind"] for item in visible}
        assert {"relic", "potion", "card_reward", "curse", "gold"} <= kinds

        assert any(
            item["item_kind"] == "potion"
            and item["potion_rarity"] == "Rare"
            and item["visual_variant"] == "rare_potion"
            for item in visible
        )
        assert any(
            item["item_kind"] == "card_reward"
            and item["card_rarity"] == "Rare"
            and item["visual_variant"] == "rare_card_reward"
            and item["reward_preview"]["card_choices"] == 3
            for item in visible
        )
        assert any(
            item["item_kind"] == "gold"
            and item["gold_size"] == "big"
            and item["gold_amount"] == 30
            and item["visual_variant"] == "big_gold"
            for item in visible
        )
        assert any(
            item["item_kind"] == "curse"
            and item["is_good"] is False
            and item["reward_preview"]["curse_card"] == "Doubt"
            for item in visible
        )
        assert any(
            item["item_kind"] == "relic"
            and item["reward_preview"] == {"category": "relic"}
            for item in visible
        )


class TestMorphicGrove:
    def test_morphic_grove_option_description_uses_engine_loc_vars(self, game):
        state = game.start(seed="morphic-grove-vars")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="MORPHIC_GROVE")

        group = next(o for o in state["options"] if o["title"] == "Group")

        assert "{Gold}" not in group["description"]
        assert "Lose 999 Gold." in group["description"]
        assert "Transform 2 cards." in group["description"]
        assert group["vars"]["Gold"] == 999


class TestByrdonisNest:
    def test_take_option_names_byrdonis_egg(self, game):
        state = game.start(seed="byrdonis-nest-card-var")
        game.skip_neow(state)
        state = game.enter_room("event", event="BYRDONIS_NEST")

        take = next(o for o in state["options"] if o["title"] == "Take the Egg")

        assert take["vars"]["Card"] == "Byrdonis Egg"
        assert "Byrdonis Egg" in take["description"]

    def test_eat_option_finishes_event(self, game):
        state = game.start(seed="byrdonis-nest-eat")
        game.skip_neow(state)
        state = game.enter_room("event", event="BYRDONIS_NEST")
        eat = next(o for o in state["options"] if o["title"] == "Eat the Egg")

        state = game.act("choose_option", option_index=eat["index"])

        assert state["decision"] == "map_select"


class TestBattlewornDummy:
    def test_timeout_returns_event_defeat_page_not_combat_rewards(self, game):
        state = game.start(seed="battleworn-dummy-timeout")
        game.skip_neow(state)
        state = game.enter_room("event", event="BATTLEWORN_DUMMY")

        setting = next(o for o in state["options"] if o["title"] == "Setting 3")
        state = game.act("choose_option", option_index=setting["index"])
        assert state["decision"] == "combat_play"

        for _ in range(3):
            state = game.act("end_turn")

        assert state["decision"] == "event_result"
        assert state["event_name"] == "Battleworn Dummy"
        assert "YOU ARE WEAK" in state["description"]
        assert state["options"] == [{"index": 0, "title": "Proceed", "is_locked": False}]
        assert not any(
            reward.get("kind") in {"potion", "card_reward", "relic"}
            for reward in state.get("rewards", [])
        )

        state = game.act("choose_option", option_index=0)
        assert state["decision"] == "map_select"

    def test_timeout_proceed_does_not_resume_finished_event_again(self):
        result, outputs = run_headless_jsonl([
            {"cmd": "start_run", "character": "Defect", "seed": "battleworn-dummy-proceed-stderr", "lang": "en"},
            {"cmd": "action", "action": "choose_option", "args": {"option_index": 0}},
            {"cmd": "enter_room", "type": "event", "event": "BATTLEWORN_DUMMY"},
            {"cmd": "action", "action": "choose_option", "args": {"option_index": 2}},
            {"cmd": "action", "action": "end_turn"},
            {"cmd": "action", "action": "end_turn"},
            {"cmd": "action", "action": "end_turn"},
            {"cmd": "action", "action": "proceed"},
        ])

        assert outputs[-2]["decision"] == "event_result"
        assert outputs[-1]["decision"] == "map_select"
        assert "Tried to set event options after event was finished" not in result.stderr
        assert "BattlewornDummy+<Resume" not in result.stderr


class TestBugslayer:
    def test_technique_options_name_reward_cards(self, game):
        state = game.start(seed="bugslayer-card-vars")
        game.skip_neow(state)
        state = game.enter_room("event", event="BUGSLAYER")

        extermination = next(o for o in state["options"] if o["title"] == "Learn Extermination Technique")
        squash = next(o for o in state["options"] if o["title"] == "Learn Squash Technique")

        assert extermination["vars"]["Card1"] == "Exterminate"
        assert "Exterminate" in extermination["description"]
        assert squash["vars"]["Card2"] == "Squash"
        assert "Squash" in squash["description"]


class TestLostWisp:
    def test_capture_option_names_curse_and_relic(self, game):
        state = game.start(seed="lost-wisp-vars")
        game.skip_neow(state)
        state = game.enter_room("event", event="LOST_WISP")

        capture = next(o for o in state["options"] if o["title"] == "Capture the Wisp")

        assert capture["vars"]["Curse"] == "Decay"
        assert capture["vars"]["Relic"] == "Lost Wisp"
        assert "Decay" in capture["description"]
        assert "Lost Wisp" in capture["description"]


class TestColossalFlower:
    def test_pollinous_core_option_exports_relic_hover_tip(self, game):
        state = game.start(seed="colossal-flower-hover-tip")
        game.skip_neow(state)
        state = game.enter_room("event", event="COLOSSAL_FLOWER")

        state = game.act("choose_option", option_index=1)
        state = game.act("choose_option", option_index=1)
        center = next(o for o in state["options"] if o["title"] == "Enter the Center")

        tips = center["hover_tips"]
        relic = next(t for t in tips if t["kind"] == "relic" and t["id"] == "POLLINOUS_CORE")

        assert relic["name"] == "Pollinous Core"
        assert relic["description"]
        assert "{" not in relic["description"]
        assert relic["vars"]["Cards"] == 2
        assert relic["vars"]["Turns"] == 4


class TestRanwidTheElder:
    def test_give_potion_option_names_current_potion(self, game):
        state = game.start(seed="ranwid-vars")
        game.skip_neow(state)
        game.set_player(potions=["BLOOD_POTION"])
        state = game.enter_room("event", event="RANWID_THE_ELDER")

        potion = next(o for o in state["options"] if o["text_key"].endswith(".POTION"))
        gold = next(o for o in state["options"] if o["text_key"].endswith(".GOLD"))

        assert potion["vars"]["Potion"] == "Blood Potion"
        assert potion["title"] == "Give Blood Potion"
        assert gold["title"] == "Give 100 Gold"

    def test_relic_cost_hover_tip_preserves_owned_counter_state(self, game):
        state = game.start(seed="ranwid-counter-tip")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            relics=["PEN_NIB", "WINGED_BOOTS"],
            deck=["ANGER"] * 10,
            potions=["ENERGY_POTION"],
            gold=200,
        )
        state = game.enter_room("combat", encounter="TEST_SUBJECT_BOSS")

        for _ in range(6):
            attacks = [
                card for card in state["hand"]
                if card["id"] == "CARD.ANGER" and card["can_play"]
            ]
            if not attacks:
                state = game.act("end_turn")
                attacks = [
                    card for card in state["hand"]
                    if card["id"] == "CARD.ANGER" and card["can_play"]
                ]
            state = game.act(
                "play_card",
                card_index=attacks[0]["index"],
                target_index=state["enemies"][0]["index"],
            )

        owned = next(
            relic for relic in state["player"]["relics"]
            if relic["id"] == "PEN_NIB"
        )
        state = game.enter_room("event", event="RANWID_THE_ELDER")
        option = next(
            option for option in state["options"]
            if option["text_key"].endswith(".RELIC")
        )
        tip = next(tip for tip in option["hover_tips"] if tip.get("id") == "PEN_NIB")

        assert option["title"] == "Give Pen Nib"
        assert owned["display_amount"] == 6
        assert tip["display_amount"] == owned["display_amount"]


class TestFutureOfPotions:
    def test_potion_options_export_source_and_result_details(self, game):
        state = game.start(seed="future-of-potions-vars")
        game.skip_neow(state)
        game.set_player(potions=["FLEX_POTION", "GAMBLERS_BREW"])
        state = game.enter_room("event", event="THE_FUTURE_OF_POTIONS")

        potion_options = [
            option for option in state["options"]
            if option["text_key"].endswith(".POTION")
        ]

        assert len(potion_options) == 2
        assert potion_options[0]["vars"]["Potion"] == "Flex Potion"
        assert potion_options[1]["vars"]["Potion"] == "Gambler's Brew"
        for option in potion_options:
            assert option["vars"]["Rarity"]
            assert option["vars"]["Type"]
            assert "{" not in option["title"]
            assert "{" not in option["description"]

    def test_upgraded_event_card_reward_exports_upgrade_flag(self, game):
        state = game.start(seed="future-of-potions-upgraded-card")
        game.skip_neow(state)
        game.set_player(potions=["LIQUID_MEMORIES"])
        state = game.enter_room("event", event="THE_FUTURE_OF_POTIONS")

        rare_option = next(option for option in state["options"]
                           if option["vars"]["Rarity"] == "Rare")
        state = game.act("choose_option", option_index=rare_option["index"])

        assert state["decision"] == "card_reward"
        assert all(card["upgraded"] is True for card in state["cards"])


class TestRelicTrader:
    def test_trade_options_export_owned_and_new_relic_details(self, game):
        state = game.start(seed="relic-trader-preview")
        game.skip_neow(state)
        game.set_player(relics=[
            "ICE_CREAM",
            "PEN_NIB",
            "STRIKE_DUMMY",
            "BURNING_BLOOD",
            "BRONZE_SCALES",
            "CENTENNIAL_PUZZLE",
        ])
        state = game.enter_room("event", event="RELIC_TRADER")

        trade_options = [
            option for option in state["options"]
            if option["text_key"].startswith("RELIC_TRADER.pages.INITIAL.options.")
        ]

        assert len(trade_options) == 3
        for option in trade_options:
            trade = option["relic_trade"]
            assert trade["owned"]["name"] in option["description"]
            assert trade["owned"]["description"]
            assert trade["new"]["name"] in option["description"]
            assert trade["new"]["description"]

    def test_trade_options_resolve_static_hover_tips(self, game):
        state = game.start(seed="relic-trader-hover-tips")
        game.skip_neow(state)
        game.set_player(relics=[
            "ANCHOR",
            "BURNING_BLOOD",
            "PETRIFIED_TOAD",
            "HORN_CLEAT",
            "VAMBRACE",
            "MOLTEN_EGG",
        ])
        state = game.enter_room("event", event="RELIC_TRADER")

        hover_tips = [
            tip
            for option in state["options"]
            for tip in option["hover_tips"]
        ]
        block_tip = next(tip for tip in hover_tips if tip.get("title") == "Block")

        assert block_tip["description"]
        assert "BLOCK." not in block_tip["description"]
        assert all("[" not in (tip.get("description") or "") for tip in hover_tips)

    def test_trade_hover_tip_preserves_owned_counter_relic_state(self, game):
        state = game.start(seed="relic-trader-counter-tip")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            relics=[
                "PEN_NIB",
                "ICE_CREAM",
                "STRIKE_DUMMY",
                "BURNING_BLOOD",
                "BRONZE_SCALES",
                "CENTENNIAL_PUZZLE",
            ],
            deck=["ANGER"] * 10,
        )
        state = game.enter_room("combat", encounter="TEST_SUBJECT_BOSS")

        for _ in range(6):
            attacks = [
                card for card in state["hand"]
                if card["id"] == "CARD.ANGER" and card["can_play"]
            ]
            if not attacks:
                state = game.act("end_turn")
                attacks = [
                    card for card in state["hand"]
                    if card["id"] == "CARD.ANGER" and card["can_play"]
                ]
            state = game.act(
                "play_card",
                card_index=attacks[0]["index"],
                target_index=state["enemies"][0]["index"],
            )

        state = game.enter_room("event", event="RELIC_TRADER")
        option = next(
            option for option in state["options"]
            if option.get("relic_trade", {}).get("owned", {}).get("id") == "PEN_NIB"
        )
        owned = option["relic_trade"]["owned"]
        tip = next(tip for tip in option["hover_tips"] if tip.get("id") == "PEN_NIB")

        assert owned["display_amount"] == 6
        assert tip["display_amount"] == owned["display_amount"]


class TestJungleMazeAdventure:
    def test_join_forces_awards_gold_and_finishes_event(self, game):
        state = game.start(seed="jungle-maze-join")
        game.skip_neow(state)
        state = game.enter_room("event", event="JUNGLE_MAZE_ADVENTURE")
        gold_before = state["player"]["gold"]

        join_forces = next(o for o in state["options"] if o["title"] == "Join Forces")
        gold_reward = join_forces["vars"]["JoinForcesGold"]
        state = game.act("choose_option", option_index=join_forces["index"])

        assert state["decision"] == "map_select"
        assert state["player"]["gold"] == gold_before + gold_reward


class TestSpiritGrafter:
    def test_let_it_in_heals_adds_metamorphosis_and_finishes_event(self, game):
        state = game.start(seed="spirit-grafter-let-in")
        game.skip_neow(state)
        game.set_player(hp=60, max_hp=87)
        state = game.enter_room("event", event="SPIRIT_GRAFTER")

        let_it_in = next(o for o in state["options"] if o["title"] == "Let It In")
        heal_amount = let_it_in["vars"]["LetItInHealAmount"]
        deck_size_before = state["player"]["deck_size"]

        state = game.act("choose_option", option_index=let_it_in["index"])

        assert state["decision"] == "map_select"
        assert state["player"]["hp"] == min(87, 60 + heal_amount)
        assert state["player"]["deck_size"] == deck_size_before + 1
        assert any(c["name"] == "Metamorphosis" for c in state["player"]["deck"])


class TestSapphireSeed:
    def test_plant_option_names_sown_enchantment(self, game):
        state = game.start(seed="sapphire-seed-enchantment-var")
        game.skip_neow(state)
        state = game.enter_room("event", event="SAPPHIRE_SEED")

        plant = next(o for o in state["options"] if o["title"] == "Plant and Nourish")

        assert plant["vars"]["Enchantment"] == "Sown"
        assert "Sown" in plant["description"]
        assert "with 0" not in plant["description"]
        sown_tip = next(tip for tip in plant["hover_tips"] if tip["title"] == "Sown")
        assert "{Amount:energyIcons()}" not in sown_tip["description"]
        assert "[E]" in sown_tip["description"]
        assert "energy_icon.png" not in sown_tip["description"]
        assert "1 Energy" not in sown_tip["description"]

    def test_sown_card_upgrade_preview_preserves_enchantment_text(self, game):
        state = game.start(seed="sapphire-seed-upgrade-preview")
        game.skip_neow(state)
        game.set_player(deck=[
            "CINDER",
            "STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
            "BASH",
        ])
        state = game.enter_room("event", event="SAPPHIRE_SEED")

        plant = next(o for o in state["options"] if o["title"] == "Plant and Nourish")
        state = game.act("choose_option", option_index=plant["index"])
        cinder = next(c for c in state["cards"] if c["name"] == "Cinder")
        state = game.act("select_cards", indices=str(cinder["index"]))
        state = game.enter_room("rest_site")
        smith = next(o for o in state["options"] if o["title"] == "Smith")
        state = game.act("choose_option", option_index=smith["index"])

        cinder = next(c for c in state["cards"] if c["name"] == "Cinder")
        assert "[E]" in cinder["description"]
        assert "[E]" in cinder["after_upgrade"]["description"]
        assert "energy_icon.png" not in cinder["description"]
        assert "energy_icon.png" not in cinder["after_upgrade"]["description"]


class TestWongos:
    def test_bargain_bin_does_not_export_featured_random_relic_var(self, game):
        state = game.start(seed="wongos-random-relic-vars")
        game.skip_neow(state)
        game.set_player(gold=999)
        state = game.enter_room("event", event="WELCOME_TO_WONGOS")

        bargain = next(
            opt for opt in state["options"]
            if str(opt.get("text_key", "")).endswith(".BARGAIN_BIN")
        )
        featured = next(
            opt for opt in state["options"]
            if str(opt.get("text_key", "")).endswith(".FEATURED_ITEM")
        )

        assert "RandomRelic" not in (bargain.get("vars") or {})
        assert "random Common Relic" in bargain["description"]

        featured_vars = featured.get("vars") or {}
        assert "RandomRelic" in featured_vars
        assert "{RandomRelic}" not in featured["description"]
        assert featured_vars["RandomRelic"] in featured["description"]


class TestSelfHelpBook:
    def test_enchantment_card_select_carries_source_option_context(self, game):
        state = game.start(character="Defect", seed="self-help-book-select-context")
        game.skip_neow(state)
        game.set_player(deck=[
            "STRIKE_DEFECT",
            "DEFEND_DEFECT",
            "ZAP",
            "DUALCAST",
            "GO_FOR_THE_EYES",
        ])
        state = game.enter_room("event", event="SELF_HELP_BOOK")

        read_back = next(o for o in state["options"] if "Sharp" in o["description"])
        state = game.act("choose_option", option_index=read_back["index"])

        assert state["decision"] == "card_select"
        assert state["prompt"] == f"{read_back['title']}: {read_back['description']}"
        source = state["source_event_option"]
        assert source["title"] == read_back["title"]
        assert source["description"] == read_back["description"]
        assert source["vars"]["Enchantment1"] == "Sharp"
        sharp_tip = next(tip for tip in source["hover_tips"] if tip["title"] == "Sharp")
        assert "Increases damage" in sharp_tip["description"]

    def test_swift_upgrade_preview_does_not_duplicate_enchantment_text(self, game):
        state = game.start(seed="self-help-book-swift-upgrade-preview")
        game.skip_neow(state)
        game.set_player(deck=[
            "ROLLING_BOULDER",
            "STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
            "BASH",
        ])
        state = game.enter_room("event", event="SELF_HELP_BOOK")

        read_book = next(o for o in state["options"] if o["title"] == "Read the Entire Book")
        state = game.act("choose_option", option_index=read_book["index"])
        if state["decision"] == "card_select":
            rolling_boulder = next(c for c in state["cards"] if c["name"] == "Rolling Boulder")
            state = game.act("select_cards", indices=str(rolling_boulder["index"]))

        rolling_boulder = next(c for c in state["player"]["deck"] if c["name"] == "Rolling Boulder")
        description = rolling_boulder["after_upgrade"]["description"].lower()

        assert description.count("draw 2 cards") == 1


class TestNonupeipe:
    def test_relic_option_energy_icons_are_formatted(self, game):
        state = game.start(seed="nonupeipe-antler-5")
        game.skip_neow(state)
        state = game.enter_room("event", event="NONUPEIPE")

        antler = next(o for o in state["options"] if o["title"] == "Blessed Antler")

        assert "{Energy:energyIcons()}" not in antler["description"]
        assert "[E]" in antler["description"]
        assert "energy_icon.png" not in antler["description"]
        assert "1 Energy" not in antler["description"]


class TestPaelAncient:
    def test_tears_option_formats_energy_icons(self, game, tmp_path):
        state = game.start(
            character="Regent",
            seed="manual-regent-a10-serious-20260518-03",
            ascension=10,
        )
        state = game.skip_neow(state)

        save_path = tmp_path / "act_two_map.save"
        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["success"] is True

        save_data = json.loads(save_path.read_text())
        save_data["current_act_index"] = 1
        save_data["visited_map_coords"] = []
        save_path.write_text(json.dumps(save_data))

        state = game.send({"cmd": "load_save", "path": str(save_path), "lang": "en"})
        ancient = next(choice for choice in state["choices"] if choice["type"] == "Ancient")
        state = game.act("select_map_node", col=ancient["col"], row=ancient["row"])

        tears = next(option for option in state["options"] if option["title"] == "Pael's Tears")

        assert "{energyPrefix:energyIcons(1)}" not in tears["description"]
        assert "{Energy:energyIcons()}" not in tears["description"]
        assert "[E]" in tears["description"]
        assert "energy_icon.png" not in tears["description"]
        assert "1 Energy" not in tears["description"]

        claw = next(option for option in state["options"] if option["title"] == "Pael's Claw")
        assert "with 0" not in claw["description"]
        assert "Goopy" in claw["description"]


class TestWoodCarvings:
    def test_snake_enchantment_is_exported_on_deck_card(self, game):
        state = game.start(seed="wood-carvings-slither-export")
        game.skip_neow(state)
        game.set_player(deck=[
            "PERFECTED_STRIKE",
            "STRIKE_IRONCLAD",
            "STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
        ])
        state = game.enter_room("event", event="WOOD_CARVINGS")
        snake = next(o for o in state["options"] if o["title"] == "Snake")

        state = game.act("choose_option", option_index=snake["index"])
        perfected = next(c for c in state["cards"] if c["name"] == "Perfected Strike")
        state = game.act("select_cards", indices=str(perfected["index"]))

        deck_card = next(c for c in state["player"]["deck"] if c["name"] == "Perfected Strike")
        assert deck_card["enchantment"] == "Slither"
        assert deck_card["enchantment_id"] == "SLITHER"
        assert "randomize its cost" in deck_card["enchantment_description"]


class TestTinkerTime:
    def test_mad_science_hover_tips_preserve_event_selected_card_state(self, game):
        state = game.start(seed="tinker-time-mad-science-hover")
        game.skip_neow(state)
        state = game.enter_room("event", event="TINKER_TIME")

        accept = next(o for o in state["options"] if o["title"] == "Accept")
        state = game.act("choose_option", option_index=accept["index"])

        card_type_tips = [
            tip
            for option in state["options"]
            for tip in option.get("hover_tips", [])
            if tip.get("id") == "CARD.MAD_SCIENCE"
        ]
        assert card_type_tips
        for tip in card_type_tips:
            assert tip["type"] in {"Attack", "Skill", "Power"}
            assert "{CardType" not in tip["description"]
            if tip.get("after_upgrade"):
                assert "{CardType" not in tip["after_upgrade"]["description"]

        state = game.act("choose_option", option_index=state["options"][0]["index"])
        rider_tips = [
            tip
            for option in state["options"]
            for tip in option.get("hover_tips", [])
            if tip.get("id") == "CARD.MAD_SCIENCE"
        ]
        assert rider_tips
        for tip in rider_tips:
            assert tip["type"] in {"Attack", "Skill", "Power"}
            assert "{CardType" not in tip["description"]
            assert "???" not in tip["description"]
            if tip.get("after_upgrade"):
                assert "{CardType" not in tip["after_upgrade"]["description"]
                assert "???" not in tip["after_upgrade"]["description"]

        state = game.act("choose_option", option_index=state["options"][0]["index"])
        mad_science = next(card for card in state["player"]["deck"] if card["id"] == "CARD.MAD_SCIENCE")
        assert mad_science["type"] in {"Attack", "Skill", "Power"}
        assert "{CardType" not in mad_science["description"]
        assert "???" not in mad_science["description"]
        if mad_science.get("after_upgrade"):
            assert "{CardType" not in mad_science["after_upgrade"]["description"]
            assert "???" not in mad_science["after_upgrade"]["description"]


class TestTrial:
    def test_trial_event_description_formats_entrant_number(self, game):
        state = game.start(seed="trial-entrant-number")
        game.skip_neow(state)
        state = game.enter_room("event", event="TRIAL")

        assert state["event_name"] == "The Trial"
        assert state["description"]
        assert "{" not in state["description"]
        assert "}" not in state["description"]
        assert "Entrant " in state["description"]

    def test_accept_advances_from_initial_trial_page(self, game):
        state = game.start(seed="trial-accept-advances")
        game.skip_neow(state)
        state = game.enter_room("event", event="TRIAL")
        accept = next(opt for opt in state["options"] if opt["title"] == "Accept")

        state = game.act("choose_option", option_index=accept["index"])

        assert state["decision"] == "event_choice"
        assert state["event_name"] == "The Trial"
        assert "DECIDER" not in state["description"]
        assert any(opt["title"].startswith("DECIDE:") for opt in state["options"])
