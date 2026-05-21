"""Regression tests for treasure room relic handling."""

from conftest import run_headless_jsonl


def relic_count(state):
    return len(state.get("player", {}).get("relics", []))


def test_multiple_treasure_rooms_clear_relic_picking_session(game):
    state = game.start(seed="treasure-session-regression")
    starting_relics = relic_count(state)

    state = game.enter_room("treasure")
    assert state["decision"] == "treasure"
    assert len(state["relics"]) >= 1
    state = game.act("claim_relic", relic_index=0)
    assert state["decision"] == "map_select"
    after_first = relic_count(state)
    assert after_first == starting_relics + 1

    state = game.enter_room("treasure")
    assert state["decision"] == "treasure"
    state = game.act("claim_relic", relic_index=0)
    assert state["decision"] == "map_select"
    assert relic_count(state) == after_first + 1


def test_treasure_room_does_not_auto_claim_relic(game):
    state = game.start(seed="treasure-explicit-claim")
    starting_relics = relic_count(state)

    state = game.enter_room("treasure")

    assert state["decision"] == "treasure"
    assert relic_count(state) == starting_relics
    assert state["relics"][0]["index"] == 0
    assert state["relics"][0]["name"]


def test_treasure_relic_exports_effect_vars_and_resolved_description(game):
    state = game.start(seed="relic-vars-a")
    state = game.enter_room("treasure")

    relic = state["relics"][0]

    assert relic["id"] == "BRONZE_SCALES"
    assert relic["vars"]["ThornsPower"] == 3
    assert "{ThornsPower}" not in relic["description"]
    assert "3" in relic["description"]

    state = game.act("claim_relic", relic_index=relic["index"])
    owned = next(r for r in state["player"]["relics"] if r["id"] == "BRONZE_SCALES")

    assert owned["vars"]["ThornsPower"] == 3
    assert "{ThornsPower}" not in owned["description"]


def test_counter_relic_exports_display_amount(game):
    state = game.start(seed="relic-counter-export")
    game.skip_neow(state)
    state = game.set_player(relics=["SWORD_OF_STONE"])

    sword = next(r for r in state["player"]["relics"] if r["id"] == "SWORD_OF_STONE")
    assert sword["show_counter"] is True
    assert sword["display_amount"] == 0


def test_player_relic_descriptions_resolve_energy_icons(game):
    state = game.start(seed="relic-energy-description")
    game.skip_neow(state)
    game.set_player(relics=["BLOOD_SOAKED_ROSE"])

    state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
    rose = next(r for r in state["player"]["relics"] if r["id"] == "BLOOD_SOAKED_ROSE")

    assert "{Energy:energyIcons()}" not in rose["description"]
    assert "[E]" in rose["description"]
    assert "energy_icon.png" not in rose["description"]


def test_player_relic_descriptions_repeat_multiple_energy_icons(game):
    state = game.start(seed="relic-energy-description-multiple")
    game.skip_neow(state)
    game.set_player(relics=["VERY_HOT_COCOA"])

    state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
    cocoa = next(r for r in state["player"]["relics"] if r["id"] == "VERY_HOT_COCOA")

    assert "{Energy:energyIcons()}" not in cocoa["description"]
    assert "4[E]" not in cocoa["description"]
    assert "energy_icon.png" not in cocoa["description"]
    assert cocoa["description"].count("[E]") == 4


def test_empty_treasure_from_silver_crucible_is_explicit_and_proceeds(game):
    game.start(seed="silver-crucible-empty-chest")
    game.set_player(relics=["SILVER_CRUCIBLE"])

    state = game.enter_room("treasure")

    assert state["decision"] == "treasure"
    assert state["relics"] == []
    assert state["can_proceed"] is True
    assert [r["id"] for r in state["player"]["relics"]] == ["SILVER_CRUCIBLE"]

    state = game.act("proceed")

    assert state["decision"] == "map_select"
    assert [r["id"] for r in state["player"]["relics"]] == ["SILVER_CRUCIBLE"]


def test_direct_relic_setup_does_not_block_on_pickup_selection_relics():
    result, outputs = run_headless_jsonl(
        [
            {"cmd": "start_run", "seed": "direct-astrolabe-setup"},
            {
                "cmd": "set_player",
                "hp": 999,
                "max_hp": 999,
                "deck": ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH"],
                "relics": ["ASTROLABE"],
                "relic_setup_mode": "direct",
            },
            {"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"},
            {"cmd": "quit"},
        ],
        timeout=15,
    )

    assert result.returncode == 0
    assert outputs[2]["type"] == "ok"
    assert [r["id"] for r in outputs[2]["player"]["relics"]] == ["ASTROLABE"]
    assert outputs[3]["decision"] == "combat_play"
