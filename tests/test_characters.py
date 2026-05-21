"""Tests for all 5 characters."""
import pytest

CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


class TestCharacterMechanics:
    def test_defect_has_orbs(self, game):
        state = game.start(character="Defect", seed="dm1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert "orbs" in state or "orb_slots" in state

    def test_defect_orbs_export_engine_evoke_order(self, game):
        state = game.start(character="Defect", seed="defect-orb-order")
        game.skip_neow(state)
        game.set_player(
            relics=[],
            deck=[
                "COOLHEADED",
                "COOLHEADED",
                "GLASSWORK",
                "QUADCAST",
                "DEFEND_DEFECT",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        for name in ["Coolheaded", "Coolheaded", "Glasswork"]:
            card = next(c for c in state["hand"] if c["name"] == name)
            state = game.act("play_card", card_index=card["index"])

        orbs = state["orbs"]
        assert [orb["type"] for orb in orbs] == ["Frost", "Frost", "Glass"]
        assert orbs[0]["is_next_to_evoke"] is True
        assert orbs[0]["position_label"] == "rightmost"
        assert orbs[-1]["is_next_to_evoke"] is False
        assert orbs[-1]["position_label"] == "leftmost"

        state = game.act("end_turn")
        quadcast = next(c for c in state["hand"] if c["name"] == "Quadcast")
        state = game.act("play_card", card_index=quadcast["index"])
        assert state["player"]["block"] == 20
        assert [orb["type"] for orb in state["orbs"]] == ["Frost", "Glass"]

    def test_defect_orb_cards_export_hover_tips(self, game):
        state = game.start(character="Defect", seed="defect-orb-card-hover-tips")
        game.skip_neow(state)
        game.set_player(
            relics=[],
            deck=[
                "GLASSWORK",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        glasswork = next(c for c in state["hand"] if c["name"] == "Glasswork")
        tips = glasswork.get("hover_tips") or []
        glass = next(tip for tip in tips if tip.get("kind") == "orb" and tip.get("title") == "Glass")

        assert "Deals damage to ALL enemies" in glass["description"]

    def test_card_hover_tips_do_not_export_room_tooltips_for_plain_enemy_text(self, game):
        state = game.start(character="Defect", seed="defect-card-hover-tip-room-filter")
        game.skip_neow(state)
        game.set_player(
            relics=[],
            deck=[
                "TESLA_COIL",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        tesla = next(c for c in state["hand"] if c["name"] == "Tesla Coil")
        tips = tesla.get("hover_tips") or []

        assert not any(tip.get("id") == "ROOM_ENEMY" for tip in tips)
        assert not any(tip.get("title") == "Enemy" for tip in tips)

    def test_necrobinder_inky_hover_tip_resolves_dynamic_values(self, game):
        state = game.start(character="Necrobinder", seed="necrobinder-inky-hover-tip")
        game.skip_neow(state)
        game.set_player(
            relics=[],
            deck=[
                "BLADE_OF_INK",
                "STRIKE_NECROBINDER",
                "DEFEND_NECROBINDER",
                "DEFEND_NECROBINDER",
                "DEFEND_NECROBINDER",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        blade = next(c for c in state["hand"] if c["name"] == "Blade of Ink")
        tips = blade.get("hover_tips") or []
        inky = next(tip for tip in tips if tip.get("title") == "Inky")

        assert "{Damage}" not in inky["description"]
        assert "{WeakPower}" not in inky["description"]
        assert "additional damage" in inky["description"]

        state = game.act("play_card", card_index=blade["index"])
        shiv = next(c for c in state["hand"] if c["id"] == "CARD.SHIV")
        assert "INKY.extraCardText" not in shiv["description"]
        assert "Apply 1 Weak." in shiv["description"]

    def test_regent_has_stars(self, game):
        state = game.start(character="Regent", seed="dm2")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert "stars" in state

    def test_necrobinder_has_osty(self, game):
        state = game.start(character="Necrobinder", seed="dm3")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert "osty" in state
        assert "hp" in state["osty"]
        assert "alive" in state["osty"]


class TestFullRun:
    @pytest.mark.parametrize("character", CHARACTERS)
    @pytest.mark.slow
    def test_full_run(self, game, character):
        state = game.start(character=character, seed=f"full_{character.lower()}")
        steps = 0
        while steps < 2000:
            dec = state.get("decision", "")
            if dec == "game_over":
                assert "victory" in state
                return
            if state.get("type") == "error":
                state = game.act("proceed")
            elif dec == "combat_play":
                state = game.auto_combat(state)
            elif dec == "map_select":
                state = game.act("select_map_node",
                                 col=state["choices"][0]["col"],
                                 row=state["choices"][0]["row"])
            elif dec == "event_choice":
                opts = [o for o in state["options"] if not o.get("is_locked")]
                state = game.act("choose_option", option_index=opts[0]["index"]) if opts else game.act("leave_room")
            elif dec == "combat_reward":
                state = game.claim_combat_rewards(state)
            elif dec == "card_reward":
                state = game.act("skip_card_reward")
            elif dec == "bundle_select":
                state = game.act("select_bundle", bundle_index=0)
            elif dec == "card_select":
                if state.get("can_skip", state.get("min_select", 0) == 0):
                    state = game.act("skip_select")
                else:
                    state = game.act("select_cards", indices="0")
            elif dec == "rest_site":
                opts = [o for o in state["options"] if o.get("is_enabled")]
                state = game.act("choose_option", option_index=opts[0]["index"])
            elif dec == "shop":
                state = game.act("leave_room")
            else:
                state = game.act("proceed")
            steps += 1
        pytest.fail(f"{character} did not finish in {steps} steps")
