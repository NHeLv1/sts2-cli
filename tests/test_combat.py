"""Tests for combat scenarios."""
import json
import os
import queue
import subprocess
import tempfile
import threading

import pytest
from conftest import DOTNET, HEADLESS_DLL, LOCAL_DOTNET_DIR, STS2_CLI_ROOT, Game, run_headless_jsonl


def card_energy_cost(card, default=99):
    cost = card.get("energy_cost", card.get("cost", default))
    if isinstance(cost, (int, float)):
        return cost
    if isinstance(cost, str) and cost.upper() == "X":
        x_value = card.get("x_value", card.get("x_cost", 0))
        if isinstance(x_value, (int, float)):
            return x_value
        return 0
    return default


class HeadlessSession:
    def __init__(self):
        env = os.environ.copy()
        env["DOTNET_ROOT"] = str(LOCAL_DOTNET_DIR)
        env["PATH"] = str(LOCAL_DOTNET_DIR) + os.pathsep + env.get("PATH", "")
        env["STS2_LIB"] = str(STS2_CLI_ROOT / "lib")
        env["STS2_GAME_DIR"] = str(STS2_CLI_ROOT / "lib")
        self.stderr_file = tempfile.NamedTemporaryFile(
            mode="w+",
            encoding="utf-8",
            errors="replace",
            delete=False,
        )
        self.proc = subprocess.Popen(
            [DOTNET, str(HEADLESS_DLL)],
            cwd=STS2_CLI_ROOT,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=self.stderr_file,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )
        self.stdout_queue = queue.Queue()
        self.reader = threading.Thread(target=self._read_stdout, daemon=True)
        self.reader.start()
        ready = self.read_json()
        assert ready.get("type") == "ready"

    def _read_stdout(self):
        try:
            for line in self.proc.stdout:
                if line.startswith("{"):
                    self.stdout_queue.put(json.loads(line))
        finally:
            self.stdout_queue.put(None)

    def read_json(self, timeout=25):
        try:
            item = self.stdout_queue.get(timeout=timeout)
        except queue.Empty as exc:
            raise TimeoutError("Timed out waiting for headless JSON response") from exc
        if item is None:
            raise RuntimeError("EOF from headless process")
        return item

    def send(self, cmd):
        self.proc.stdin.write(json.dumps(cmd) + "\n")
        self.proc.stdin.flush()
        return self.read_json()

    def skip_neow(self, state):
        for _ in range(20):
            decision = state.get("decision")
            if decision == "map_select":
                return state
            if decision == "event_choice":
                options = [option for option in state["options"] if not option.get("is_locked")]
                state = self.send({
                    "cmd": "action",
                    "action": "choose_option",
                    "args": {"option_index": options[0]["index"]},
                })
            elif decision == "combat_reward":
                rewards = state.get("rewards") or []
                if not rewards:
                    state = self.send({"cmd": "action", "action": "proceed"})
                else:
                    state = self.send({
                        "cmd": "action",
                        "action": "claim_reward",
                        "args": {"reward_index": rewards[0]["index"]},
                    })
            elif decision == "card_reward":
                state = self.send({"cmd": "action", "action": "skip_card_reward"})
            elif decision == "bundle_select":
                state = self.send({"cmd": "action", "action": "select_bundle", "args": {"bundle_index": 0}})
            elif decision == "card_select":
                action = "skip_select" if state.get("can_skip", state.get("min_select", 0) == 0) else "select_cards"
                args = {} if action == "skip_select" else {"indices": "0"}
                state = self.send({"cmd": "action", "action": action, "args": args})
            else:
                state = self.send({"cmd": "action", "action": "proceed"})
        return state

    def close(self):
        if self.proc.poll() is None:
            try:
                self.proc.stdin.write('{"cmd":"quit"}\n')
                self.proc.stdin.flush()
            except Exception:
                pass
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=5)
        self.reader.join(timeout=1)
        self.stderr_file.flush()
        self.stderr_file.seek(0)
        stderr = self.stderr_file.read()
        path = self.stderr_file.name
        self.stderr_file.close()
        try:
            os.unlink(path)
        except OSError:
            pass
        return stderr


class TestCombatStructure:
    def test_combat_play_fields(self, game):
        state = game.start(seed="cs1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert state["decision"] == "combat_play"
        for key in ("round", "energy", "max_energy", "hand", "enemies",
                    "player", "draw_pile_count", "discard_pile_count", "player_powers"):
            assert key in state, f"Missing: {key}"

    def test_draw_and_discard_pile_cards_are_exported(self, game):
        state = game.start(seed="pile-details")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        assert state["draw_pile_count"] == len(state["draw_pile"])
        assert state["draw_pile"]
        assert all(card["name"] and card["description"] for card in state["draw_pile"])

        strike = next(card for card in state["hand"] if card["name"] == "Strike")
        state = game.act("play_card", card_index=strike["index"], target_index=0)

        assert state["discard_pile_count"] == len(state["discard_pile"])
        assert state["discard_pile"]
        assert any(card["name"] == "Strike" for card in state["discard_pile"])

    def test_card_fields(self, game):
        state = game.start(seed="cs2")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        for card in state["hand"]:
            assert isinstance(card["name"], str)
            assert "cost" in card
            assert "can_play" in card
            assert card["type"] in ("Attack", "Skill", "Power", "Status", "Curse")

    def test_enemy_fields(self, game):
        state = game.start(seed="cs3")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        for e in state["enemies"]:
            assert "instance_id" in e
            assert isinstance(e["name"], str)
            assert e["hp"] > 0
            assert e["max_hp"] > 0
            assert "block" in e

    def test_enemy_instance_ids_survive_target_reindexing(self, game):
        state = game.start(seed="enemy-instance-id-export")
        game.skip_neow(state)
        game.set_player(hp=999, max_hp=999, deck=["BLUDGEON"] * 5)
        state = game.enter_room("combat", encounter="SLIMES_WEAK")

        initial_ids = [enemy["instance_id"] for enemy in state["enemies"]]
        assert len(initial_ids) >= 2
        assert len(initial_ids) == len(set(initial_ids))

        target = state["enemies"][0]
        bludgeon = next(card for card in state["hand"] if card["name"] == "Bludgeon")
        state = game.act("play_card", card_index=bludgeon["index"], target_index=target["index"])

        assert state["decision"] == "combat_play"
        remaining_ids = [enemy["instance_id"] for enemy in state["enemies"]]
        assert target["instance_id"] not in remaining_ids
        assert set(remaining_ids).issubset(set(initial_ids))
        assert [enemy["index"] for enemy in state["enemies"]] == list(range(len(state["enemies"])))

    def test_enemy_name_interpolates_dynamic_vars(self, game):
        state = game.start(seed="test-subject-enemy-name")
        state = game.enter_room("combat", encounter="TEST_SUBJECT_BOSS")

        assert state["decision"] == "combat_play"
        names = [enemy["name"] for enemy in state["enemies"]]
        assert any("Test Subject" in name for name in names)
        assert all("{" not in name and "}" not in name for name in names)
        assert all("#C" not in name for name in names)

    def test_enemy_name_interpolates_after_test_subject_adaptation(self):
        game = Game()
        try:
            self._assert_test_subject_adaptation_names_are_resolved(game)
        finally:
            game.close()

    def _assert_test_subject_adaptation_names_are_resolved(self, game):
        state = game.start(seed="test-subject-phase-name")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            relics=["BURNING_BLOOD", "LANTERN"],
            deck=[
                "BLOODLETTING",
                "BLOODLETTING",
                "PERFECTED_STRIKE",
                "PERFECTED_STRIKE",
                "PERFECTED_STRIKE",
                *(["STRIKE_IRONCLAD"] * 30),
            ],
        )
        state = game.enter_room("combat", encounter="TEST_SUBJECT_BOSS")

        for _ in range(80):
            names = [enemy["name"] for enemy in state.get("enemies", [])]
            assert all("{" not in name and "}" not in name for name in names)
            if any(enemy.get("max_hp", 0) >= 300 for enemy in state.get("enemies", [])):
                return

            if state.get("decision") != "combat_play":
                state = game.act("proceed")
                continue

            playable = [
                card for card in state["hand"]
                if card.get("can_play") and card_energy_cost(card) <= state.get("energy", 0)
            ]
            if not playable:
                state = game.act("end_turn")
                continue

            playable.sort(key=lambda card: (
                0 if card["name"] == "Bloodletting" else
                1 if card["name"] == "Perfected Strike" else
                2 if card["type"] == "Attack" else
                3,
                card_energy_cost(card),
            ))
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy":
                args["target_index"] = 0
            state = game.act("play_card", **args)

        pytest.fail("Test Subject did not reach the 300 HP adaptation phase")

    def test_enemy_state_exports_next_move_name(self, game):
        state = game.start(seed="devoted-sculptor-move-name")
        state = game.enter_room("combat", encounter="DEVOTED_SCULPTOR_WEAK")

        assert state["decision"] == "combat_play"
        enemy = state["enemies"][0]
        assert enemy["move_id"] == "FORBIDDEN_INCANTATION_MOVE"
        assert enemy["move_name"] == "Forbidden Incantation"

    def test_enemy_move_name_humanizes_unlocalized_move_id(self, game):
        state = game.start(seed="slime-move-name")
        state = game.enter_room("combat", encounter="SLIMES_WEAK")

        butt_move = next(
            enemy for enemy in state["enemies"]
            if enemy.get("move_id") == "BUTT_MOVE"
        )
        assert butt_move["move_name"] == "Butt"

    def test_multi_hit_intent_exports_per_hit_and_total_damage(self, game):
        state = game.start(seed="multi-hit-intent")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="MAWLER_NORMAL")

        intent = next(
            it for it in state["enemies"][0]["intents"]
            if it["type"] == "Attack"
        )
        assert intent["hits"] == 2
        assert intent["damage"] == 4
        assert intent["total_damage"] == 8

    def test_bag_of_marbles_applies_vulnerable_at_combat_start(self, game):
        state = game.start(seed="bag-of-marbles-start")
        game.skip_neow(state)
        game.set_player(relics=["BAG_OF_MARBLES"])
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        assert state["decision"] == "combat_play"
        enemy = state["enemies"][0]
        powers = enemy.get("powers") or []
        vulnerable = next((p for p in powers if p.get("name") == "Vulnerable"), None)
        assert vulnerable is not None
        assert vulnerable.get("amount") == 1


class TestPlayCards:
    def test_enemy_target_card_requires_target_index(self, game):
        state = game.start(seed="explicit-card-target")
        game.skip_neow(state)
        game.set_player(deck=["STRIKE_IRONCLAD"] * 5)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        strike = next(c for c in state["hand"] if c["name"] == "Strike")

        result = game.act("play_card", card_index=strike["index"])

        assert result["type"] == "error"
        assert "target_index" in result["message"]

    def test_all_enemies_card_does_not_require_target_index(self, game):
        state = game.start(seed="whirlwind-all-enemies")
        game.skip_neow(state)
        game.set_player(deck=["WHIRLWIND"] * 5)
        state = game.enter_room("combat", encounter="SLIMES_WEAK")

        whirlwind = next(c for c in state["hand"] if c["name"] == "Whirlwind")
        hp_before = sum(e["hp"] for e in state["enemies"])

        result = game.act("play_card", card_index=whirlwind["index"])

        assert result.get("type") != "error"
        assert whirlwind["target_type"] == "AllEnemies"
        assert sum(e["hp"] for e in result.get("enemies", [])) < hp_before

    def test_played_card_returned_to_hand_is_successful_play(self, game):
        state = game.start(character="Defect", seed="feral-returned-card")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "FERAL",
                "GO_FOR_THE_EYES",
                "BLOODLETTING",
                "BLOODLETTING",
                "BLOODLETTING",
            ],
        )
        game.set_draw_order([
            "FERAL",
            "GO_FOR_THE_EYES",
            "BLOODLETTING",
            "BLOODLETTING",
            "BLOODLETTING",
        ])
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        feral = next(card for card in state["hand"] if card["id"] == "CARD.FERAL")
        state = game.act("play_card", card_index=feral["index"])

        while any(card["id"] == "CARD.BLOODLETTING" for card in state["hand"]):
            bloodletting = next(card for card in state["hand"] if card["id"] == "CARD.BLOODLETTING")
            state = game.act("play_card", card_index=bloodletting["index"])

        go_for_the_eyes = next(card for card in state["hand"] if card["id"] == "CARD.GO_FOR_THE_EYES")
        target = state["enemies"][0]
        hp_before = target["hp"]

        assert go_for_the_eyes["index"] == 0

        result = game.act("play_card", card_index=go_for_the_eyes["index"], target_index=target["index"])

        assert result.get("type") != "error"
        assert result["decision"] == "combat_play"
        assert result["enemies"][0]["hp"] < hp_before
        assert any(card["id"] == "CARD.GO_FOR_THE_EYES" for card in result["hand"])

    def test_play_card_costs_energy(self, game):
        state = game.start(seed="cp1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        energy_before = state["energy"]
        playable = [c for c in state["hand"] if c.get("can_play") and card_energy_cost(c) <= energy_before]
        assert playable
        card = playable[0]
        args = {"card_index": card["index"]}
        if card.get("target_type") == "AnyEnemy":
            args["target_index"] = state["enemies"][0]["index"]
        state = game.act("play_card", **args)
        if state["decision"] == "combat_play":
            assert state["energy"] == energy_before - card_energy_cost(card)

    def test_play_attack_reduces_enemy_hp(self, game):
        state = game.start(seed="cp2")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        target = state["enemies"][0]
        hp_before = target["hp"]
        attacks = [c for c in state["hand"] if c.get("can_play") and c["type"] == "Attack"
                   and card_energy_cost(c) <= state["energy"]]
        if not attacks:
            pytest.skip("No attacks in hand")
        card = attacks[0]
        args = {"card_index": card["index"]}
        if card.get("target_type") == "AnyEnemy":
            args["target_index"] = target["index"]
        state = game.act("play_card", **args)
        if state["decision"] == "combat_play":
            new_target = next((e for e in state["enemies"] if e["index"] == target["index"]), None)
            if new_target and target.get("block", 0) == 0:
                assert new_target["hp"] < hp_before

    def test_play_defend_adds_block(self, game):
        state = game.start(seed="cp3")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        block_before = state["player"].get("block", 0)
        defends = [c for c in state["hand"] if c.get("can_play") and c["type"] == "Skill"
                   and card_energy_cost(c) <= state["energy"]]
        if not defends:
            pytest.skip("No skill cards")
        state = game.act("play_card", card_index=defends[0]["index"])
        if state["decision"] == "combat_play":
            assert state["player"].get("block", 0) >= block_before


class TestTurnFlow:
    def test_end_turn_advances_round(self, game):
        state = game.start(seed="tf1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        rnd = state["round"]
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            assert state["round"] == rnd + 1

    def test_end_turn_resets_energy(self, game):
        state = game.start(seed="tf2")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        max_e = state["max_energy"]
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            assert state["energy"] == max_e

    def test_end_turn_draws_new_hand(self, game):
        state = game.start(seed="tf3")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            assert len(state["hand"]) > 0


class TestCombatEnd:
    def test_win_combat_leads_to_reward(self, game):
        state = game.start(seed="cw1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.auto_play_combat(state)
        assert state["decision"] in ("combat_reward", "card_reward", "map_select", "card_select", "bundle_select")

    def test_player_powers_after_enemy_debuff(self, game):
        """Shrinker Beetle applies Shrink debuff to player after its turn."""
        state = game.start(seed="ep1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        # End turn so beetle acts (applies Shrink to player)
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            pp = state.get("player_powers") or []
            assert len(pp) > 0, "Expected player debuff after Shrinker Beetle turn"
            for pw in pp:
                assert "name" in pw
                assert "amount" in pw
                assert "description" in pw
                assert "{" not in pw["description"]
                assert "}" not in pw["description"]


class TestCombatEdgeCases:
    def test_chomper_screech_talk_vfx_does_not_force_game_over_headless(self, game):
        state = game.start(seed="chomper-screech-talk")
        game.skip_neow(state)
        game.set_player(
            hp=80,
            max_hp=80,
            deck=["DEFEND_IRONCLAD"] * 20,
        )
        state = game.enter_room("combat", encounter="CHOMPERS_NORMAL")

        state = game.act("end_turn")

        assert state.get("type") != "error"
        assert state.get("decision") != "game_over"
        assert state["decision"] == "combat_play"
        assert state["player"]["hp"] > 0

    def test_bygone_effigy_wake_talk_vfx_does_not_deadlock_headless(self, game):
        state = game.start(seed="effigy-wake-talk")
        game.skip_neow(state)
        game.set_player(
            hp=80,
            max_hp=80,
            deck=["BREAKTHROUGH"] * 20,
        )
        state = game.enter_room("combat", encounter="BYGONE_EFFIGY_ELITE")

        for _ in range(4):
            while state.get("decision") == "combat_play":
                playable_attacks = [
                    card for card in state.get("hand", [])
                    if card.get("type") == "Attack"
                    and card.get("can_play")
                    and card_energy_cost(card) <= state.get("energy", 0)
                ]
                if not playable_attacks:
                    break
                card = playable_attacks[0]
                args = {"card_index": card["index"]}
                if card.get("target_type") == "AnyEnemy":
                    args["target_index"] = state["enemies"][0]["index"]
                state = game.act("play_card", **args)

            assert state.get("decision") == "combat_play"
            state = game.act("end_turn")
            assert state.get("decision") != "game_over"
            assert state.get("type") != "error"

        assert state["decision"] == "combat_play"
        assert state["player"]["hp"] > 0

    def test_kin_priest_ritual_talk_vfx_does_not_deadlock_headless(self, game):
        state = game.start(seed="kin-ritual-talk")
        game.skip_neow(state)
        game.set_player(
            hp=80,
            max_hp=80,
            deck=["DEFEND_IRONCLAD"] * 10 + ["SHRUG_IT_OFF"] * 10,
        )
        state = game.enter_room("combat", encounter="THE_KIN_BOSS")

        for _ in range(4):
            while state.get("decision") == "combat_play":
                playable_skills = [
                    card for card in state.get("hand", [])
                    if card.get("type") == "Skill"
                    and card.get("can_play")
                    and card_energy_cost(card) <= state.get("energy", 0)
                ]
                if not playable_skills:
                    break
                state = game.act("play_card", card_index=playable_skills[0]["index"])

            assert state.get("decision") == "combat_play"
            state = game.act("end_turn")
            assert state.get("type") != "error"

        assert state["decision"] == "combat_play"
        assert state["round"] == 5
        assert state["player"]["hp"] > 0

    def test_end_turn_during_card_select_keeps_selection(self, game):
        state = game.start(seed="pending-select-end-turn")
        game.skip_neow(state)
        game.set_player(deck=[
            "BURNING_PACT",
            "STRIKE_IRONCLAD",
            "DEFEND_IRONCLAD",
            "DEFEND_IRONCLAD",
            "STRIKE_IRONCLAD",
        ])
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        burning_pact = next(c for c in state["hand"] if c["name"] == "Burning Pact")

        state = game.act("play_card", card_index=burning_pact["index"])

        assert state["decision"] == "card_select"
        state = game.act("end_turn")
        assert state["decision"] == "card_select"
        assert state["player"]["hp"] > 0

    def test_exhaust_all_and_end_turn(self, game):
        state = game.start(seed="ce1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        for _ in range(20):
            if state.get("decision") != "combat_play":
                break
            playable = [c for c in state["hand"] if c.get("can_play") and card_energy_cost(c) <= state["energy"]]
            if not playable:
                break
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy" and state["enemies"]:
                args["target_index"] = state["enemies"][0]["index"]
            state = game.act("play_card", **args)
        if state.get("decision") == "combat_play":
            state = game.act("end_turn")
            assert state.get("type") != "error"

    def test_played_exhaust_status_exports_exhaust_pile(self, game):
        state = game.start(seed="slimed-exhaust-pile-export")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "SLIMED",
                "STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD",
                "DEFEND_IRONCLAD",
                "STRIKE_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        slimed = next(card for card in state["hand"] if card["name"] == "Slimed")

        state = game.act("play_card", card_index=slimed["index"])

        assert state["exhaust_pile_count"] == 1
        assert state["exhaust_pile"][0]["name"] == "Slimed"

    def test_many_cards_per_turn(self, game):
        """Play all playable cards in a single turn without errors."""
        state = game.start(seed="inf1")
        game.skip_neow(state)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        plays = 0
        for _ in range(20):
            if state.get("decision") != "combat_play":
                break
            playable = [c for c in state["hand"] if c.get("can_play")
                        and card_energy_cost(c) <= state["energy"] and c["type"] not in ("Status", "Curse")]
            if not playable:
                break
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy" and state["enemies"]:
                args["target_index"] = state["enemies"][0]["index"]
            state = game.act("play_card", **args)
            plays += 1
            assert state.get("type") != "error", f"Error after {plays} plays: {state.get('message')}"
        assert plays >= 2

    def test_infinite_card_loop(self, game):
        """Pommel Strike + Bloodletting infinite loop doesn't crash.

        Pommel Strike (1e): damage + draw 1
        Bloodletting (0e): lose HP + gain 2 energy
        Each cycle: net +1 energy, draws next card. Truly infinite.
        """
        state = game.start(seed="inf2")
        game.skip_neow(state)
        game.set_player(hp=80, max_hp=80, deck=["POMMEL_STRIKE"] * 5 + ["BLOODLETTING"] * 5)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        plays = 0
        for _ in range(60):
            if state.get("decision") != "combat_play":
                break
            hand = state.get("hand", [])
            energy = state.get("energy", 0)
            playable = [c for c in hand if c.get("can_play") and card_energy_cost(c) <= energy
                        and c["type"] not in ("Status", "Curse")]
            if not playable:
                break
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy" and state["enemies"]:
                args["target_index"] = state["enemies"][0]["index"]
            state = game.act("play_card", **args)
            plays += 1
            assert state.get("type") != "error", f"Error after {plays} plays: {state.get('message')}"

        # With Pommel Strike + Bloodletting, should play many cards before enemy dies
        assert plays >= 5, f"Expected infinite loop plays >= 5, got {plays}"

    def test_low_hp_death(self, game):
        """Player with 1 HP should die to any attack."""
        state = game.start(seed="ce2")
        game.skip_neow(state)
        game.set_player(hp=1)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        # Just end turn, beetle will kill us
        state = game.act("end_turn")
        # Might need another turn
        for _ in range(10):
            if state.get("decision") == "game_over":
                break
            if state.get("decision") == "combat_play":
                state = game.act("end_turn")
            else:
                break
        assert state["decision"] == "game_over"
        assert state["victory"] is False

    def test_enemy_turn_card_selection_is_returned_before_retrying_end_turn(self, game):
        state = game.start(seed="knowledge-demon-selection")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=["STRIKE_IRONCLAD"] * 5 + ["DEFEND_IRONCLAD"] * 5,
        )
        state = game.enter_room("combat", encounter="KNOWLEDGE_DEMON_BOSS")

        assert state["decision"] == "combat_play"
        assert state["enemies"][0]["move_name"] == "Curse of Knowledge"

        state = game.act("end_turn")

        assert state["decision"] == "card_select"
        assert state["min_select"] == 0
        assert state["max_select"] == 1
        assert state["cards"]

    def test_follow_up_start_of_combat_selection_is_exported_before_play(self, game):
        state = game.start(character="Silent", seed="follow-up-start-selection")
        game.skip_neow(state)
        game.set_player(
            hp=9999,
            max_hp=9999,
            relics=["GAMBLING_CHIP", "TOOLBOX"],
            deck=[
                "FAN_OF_KNIVES",
                "FAN_OF_KNIVES",
                "INFINITE_BLADES",
                "FOLLOW_THROUGH",
                "NEUTRALIZE",
                "WELL_LAID_PLANS",
                "DEFEND_SILENT",
                "SUCKER_PUNCH",
                "STRIKE_SILENT",
                "STRIKE_SILENT",
            ],
        )
        state = game.enter_room("combat", encounter="ENTOMANCER_ELITE")

        assert state["decision"] == "card_select"

        state = game.act("skip_select")

        assert state["decision"] == "card_select"
        assert state["min_select"] == 0
        assert state["max_select"] > 1

        state = game.act("skip_select")

        assert state["decision"] == "combat_play"
        card = next(card for card in state["hand"] if card["can_play"])
        args = {"card_index": card["index"]}
        if card["target_type"] == "AnyEnemy":
            args["target_index"] = 0

        state = game.act("play_card", **args)

        assert state["type"] != "error"
        assert state["decision"] == "combat_play"

    def test_soul_nexus_death_does_not_leave_combat_active(self, game):
        state = game.start(seed="soul-nexus-death-cleanup")
        game.skip_neow(state)
        game.set_player(hp=999, max_hp=999, deck=["BLUDGEON"] * 12)
        state = game.enter_room("combat", encounter="SOUL_NEXUS_ELITE")

        for _ in range(80):
            if state.get("decision") != "combat_play":
                break
            playable = [card for card in state["hand"] if card.get("can_play")]
            if playable:
                card = playable[0]
                state = game.act("play_card", card_index=card["index"], target_index=0)
            else:
                state = game.act("end_turn")

        assert state["decision"] == "combat_reward"
        assert any(
            reward["kind"] == "relic" and reward.get("name") and reward.get("description")
            for reward in state["rewards"]
        )

        state = game.claim_combat_rewards(state)
        assert state["decision"] == "card_reward"

        state = game.act("skip_card_reward")
        state = game.claim_combat_rewards(state)
        assert state["decision"] == "map_select"

        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        assert state["decision"] == "combat_play"

    def test_combat_hand_exports_stable_card_instance_ids(self, game):
        state = game.start(seed="hand-instance-id-export")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD",
                "STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD",
                "STRIKE_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        initial_ids = [card["instance_id"] for card in state["hand"]]
        assert len(initial_ids) == len(set(initial_ids))

        strike = next(card for card in state["hand"] if card["target_type"] == "AnyEnemy")
        state = game.act("play_card", card_index=strike["index"], target_index=0)

        remaining_ids = [card["instance_id"] for card in state["hand"]]
        assert strike["instance_id"] not in remaining_ids
        assert set(remaining_ids).issubset(set(initial_ids))

    def test_checkpoint_refuses_pending_card_reward_rollback(self, game, tmp_path):
        state = game.start(seed="checkpoint-pending-card-reward")
        game.skip_neow(state)
        game.set_player(hp=999, max_hp=999, deck=["BLUDGEON"] * 12)
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        state = game.auto_play_combat(state)
        state = game.claim_combat_rewards(state)

        assert state["decision"] == "card_reward"
        save_path = tmp_path / "pending-card-reward.save"
        result = game.send({"cmd": "write_continue_save", "path": str(save_path)})

        assert result["type"] == "error"
        assert "combat rewards are pending" in result["message"]
        assert not save_path.exists()

    def test_play_card_is_rejected_without_mutation_during_card_selection(self, game):
        state = game.start(seed="pending-selection-play-card-guard")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "BLOODLETTING",
                "BASH",
                "HEADBUTT",
                "TWIN_STRIKE",
                "STRIKE_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")
        bloodletting = next(card for card in state["hand"] if card["name"] == "Bloodletting")
        state = game.act("play_card", card_index=bloodletting["index"])
        bash = next(card for card in state["hand"] if card["name"] == "Bash")
        state = game.act("play_card", card_index=bash["index"], target_index=0)
        headbutt = next(card for card in state["hand"] if card["name"] == "Headbutt")
        state = game.act("play_card", card_index=headbutt["index"], target_index=0)

        assert state["decision"] == "card_select"
        invalid = game.act("play_card", card_index=0, target_index=0)
        assert invalid["type"] == "error"
        assert "card selection" in invalid["message"].lower()

        state = game.act("select_cards", indices="0")
        hand_names = [card["name"] for card in state["hand"]]
        assert "Twin Strike" in hand_names

    def test_card_select_exports_selection_prompt_from_played_card(self, game):
        state = game.start(character="Regent", seed="thinking-ahead-selection-prompt")
        game.skip_neow(state)
        game.set_player(
            deck=[
                "THINKING_AHEAD",
                "STRIKE_REGENT",
                "DEFEND_REGENT",
                "DEFEND_REGENT",
                "DEFEND_REGENT",
                "KINGLY_PUNCH",
                "ORBIT",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        thinking_ahead = next(card for card in state["hand"] if card["name"] == "Thinking Ahead")
        state = game.act("play_card", card_index=thinking_ahead["index"])

        assert state["decision"] == "card_select"
        assert state["prompt"] == "Choose a card to put on top of your Draw Pile."

    def test_delayed_discard_selection_exports_played_source_card(self, game):
        state = game.start(character="Defect", seed="hologram-selection-source")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "TURBO",
                "HOLOGRAM",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "STRIKE_DEFECT",
            ],
        )
        game.set_draw_order([
            "TURBO",
            "HOLOGRAM",
            "DEFEND_DEFECT",
            "DEFEND_DEFECT",
            "STRIKE_DEFECT",
        ])
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        turbo = next(card for card in state["hand"] if card["id"] == "CARD.TURBO")
        state = game.act("play_card", card_index=turbo["index"])
        hologram = next(card for card in state["hand"] if card["id"] == "CARD.HOLOGRAM")
        state = game.act("play_card", card_index=hologram["index"])

        assert state["decision"] == "card_select"
        assert state["prompt"] == "Choose a card to put back in your Hand."
        assert state["source_card"]["name"] == "Hologram"
        assert "Discard Pile" in state["source_card"]["description"]

    def test_off_color_star_cost_card_exports_star_resource(self, game):
        state = game.start(character="Defect", seed="off-color-star-resource")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "DECISIONS_DECISIONS",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "STRIKE_DEFECT",
                "STRIKE_DEFECT",
            ],
        )
        game.set_draw_order([
            "DECISIONS_DECISIONS",
            "DEFEND_DEFECT",
            "DEFEND_DEFECT",
            "STRIKE_DEFECT",
            "STRIKE_DEFECT",
        ])
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        decisions = next(card for card in state["hand"] if card["id"] == "CARD.DECISIONS_DECISIONS")
        assert decisions["star_cost"] == 6
        assert state["stars"] == 0
        assert decisions["can_play"] is False

    def test_start_of_turn_power_card_select_exports_prompt_and_source(self, game):
        state = game.start(character="Defect", seed="entropy-selection-source")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "ENTROPY",
                "ENTROPY",
                "ENTROPY",
                "ENTROPY",
                "ENTROPY",
                "STRIKE_DEFECT",
                "DEFEND_DEFECT",
                "ZAP",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        entropy = next(card for card in state["hand"] if card["id"] == "CARD.ENTROPY")
        state = game.act("play_card", card_index=entropy["index"])
        state = game.act("end_turn")

        assert state["decision"] == "card_select"
        assert state["prompt"] == "Choose a card to Transform."
        assert state["source_power"]["name"] == "Entropy"
        assert "Transform" in state["source_power"]["description"]

    def test_combat_card_select_exports_current_combat_context(self, game):
        state = game.start(character="Defect", seed="stratagem-selection-combat-context")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "STRATAGEM",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "DEFEND_DEFECT",
                "STRIKE_DEFECT",
            ],
        )
        game.set_draw_order([
            "STRATAGEM",
            "DEFEND_DEFECT",
            "DEFEND_DEFECT",
            "DEFEND_DEFECT",
            "DEFEND_DEFECT",
            "STRIKE_DEFECT",
        ])
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        stratagem = next(card for card in state["hand"] if card["id"] == "CARD.STRATAGEM")
        state = game.act("play_card", card_index=stratagem["index"])
        state = game.act("end_turn")

        assert state["decision"] == "card_select"
        assert state["prompt"] == "Choose a card to add into your Hand."
        assert state["source_power"]["name"] == "Stratagem"
        assert "shuffle your Draw Pile" in state["source_power"]["description"]
        combat = state["combat"]
        assert combat["enemies"][0]["name"] == "Shrinker Beetle"
        assert combat["enemies"][0]["intents"]
        assert combat["hand"]
        assert combat["draw_pile_count"] >= 0
        assert combat["discard_pile_count"] >= 0

    def test_headbutt_discard_selection_keeps_preview_stats_without_target_rows(self, game):
        state = game.start(seed="headbutt-selection-preview-stats")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "BLOODLETTING",
                "INFLAME",
                "BASH",
                "HEADBUTT",
                "TWIN_STRIKE",
            ],
        )
        state = game.enter_room("combat", encounter="ENTOMANCER_ELITE")

        bloodletting = next(card for card in state["hand"] if card["name"] == "Bloodletting")
        state = game.act("play_card", card_index=bloodletting["index"])
        inflame = next(card for card in state["hand"] if card["name"] == "Inflame")
        state = game.act("play_card", card_index=inflame["index"])
        bash = next(card for card in state["hand"] if card["name"] == "Bash")
        state = game.act("play_card", card_index=bash["index"], target_index=0)
        headbutt = next(card for card in state["hand"] if card["name"] == "Headbutt")
        state = game.act("play_card", card_index=headbutt["index"], target_index=0)

        assert state["decision"] == "card_select"
        selected_bash = next(card for card in state["cards"] if card["name"] == "Bash")
        exported_damage = selected_bash["stats"]["damage"]
        assert selected_bash["description"].startswith(f"Deal {exported_damage} damage.")
        assert "damage_by_target" not in selected_bash["stats"]

    def test_headbutt_discard_selection_upgrade_preview_uses_dynamic_context(self, game):
        state = game.start(seed="headbutt-selection-upgrade-preview")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "BLOODLETTING",
                "INFLAME",
                "PERFECTED_STRIKE",
                "HEADBUTT",
                "STRIKE_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="ENTOMANCER_ELITE")

        bloodletting = next(card for card in state["hand"] if card["name"] == "Bloodletting")
        state = game.act("play_card", card_index=bloodletting["index"])
        inflame = next(card for card in state["hand"] if card["name"] == "Inflame")
        state = game.act("play_card", card_index=inflame["index"])
        perfected = next(card for card in state["hand"] if card["name"] == "Perfected Strike")
        state = game.act("play_card", card_index=perfected["index"], target_index=0)
        headbutt = next(card for card in state["hand"] if card["name"] == "Headbutt")
        state = game.act("play_card", card_index=headbutt["index"], target_index=0)

        assert state["decision"] == "card_select"
        selected = next(card for card in state["cards"] if card["name"] == "Perfected Strike")
        upgraded = selected["after_upgrade"]
        upgraded_damage = upgraded["stats"]["calculateddamage"]

        assert upgraded_damage > upgraded["stats"]["calculationbase"]
        assert upgraded["description"].startswith(f"Deal {upgraded_damage} damage.")
        assert "calculateddamage_by_target" not in upgraded["stats"]

    def test_headbutt_discard_selection_upgrade_preview_resolves_energy_icons(self, game):
        state = game.start(seed="headbutt-selection-upgrade-energy-icons")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "BLOODLETTING",
                "BASH",
                "HEADBUTT",
                "STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="ENTOMANCER_ELITE")

        bloodletting = next(card for card in state["hand"] if card["name"] == "Bloodletting")
        state = game.act("play_card", card_index=bloodletting["index"])
        bash = next(card for card in state["hand"] if card["name"] == "Bash")
        state = game.act("play_card", card_index=bash["index"], target_index=0)
        headbutt = next(card for card in state["hand"] if card["name"] == "Headbutt")
        state = game.act("play_card", card_index=headbutt["index"], target_index=0)

        assert state["decision"] == "card_select"
        selected = next(card for card in state["cards"] if card["name"] == "Bloodletting")
        upgraded_description = selected["after_upgrade"]["description"]

        assert "{Energy:energyIcons()}" not in upgraded_description
        assert "energy_icon.png" not in upgraded_description
        assert upgraded_description.count("[E]") == 3

    def test_uninitialized_event_card_is_not_exported_as_playable(self, game):
        state = game.start(seed="mad-science-uninitialized")
        game.skip_neow(state)
        game.set_player(
            deck=[
                "MAD_SCIENCE",
                "STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD",
                "STRIKE_IRONCLAD",
                "DEFEND_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="SHRINKER_BEETLE_WEAK")

        mad_science = next(card for card in state["hand"] if card["id"] == "CARD.MAD_SCIENCE")

        assert mad_science["type"] == "None"
        assert mad_science["can_play"] is False

        result = game.act("play_card", card_index=mad_science["index"])
        assert result["type"] == "error"
        assert "uninitialized card type" in result["message"]

    def test_vantom_dismember_headless_vfx_does_not_force_game_over(self, game):
        state = game.start(seed="vantom-dismember-headless")
        game.skip_neow(state)
        game.set_player(hp=999, max_hp=999)
        state = game.enter_room("combat", encounter="VANTOM_BOSS")

        for _ in range(10):
            if state["enemies"][0]["move_name"] == "Dismember":
                break
            state = game.act("end_turn")

        assert state["enemies"][0]["move_name"] == "Dismember"

        state = game.act("end_turn")
        assert state["decision"] == "combat_play"
        assert state["player"]["hp"] > 0

    def test_kaiser_crab_headless_background_hooks_do_not_force_game_over(self, game):
        state = game.start(seed="kaiser-crab-headless-background")
        game.skip_neow(state)
        game.set_player(
            hp=999,
            max_hp=999,
            deck=[
                "DISMANTLE",
                "TWIN_STRIKE",
                "DEFEND_IRONCLAD",
                "DEFEND_IRONCLAD",
                "DEFEND_IRONCLAD",
            ],
        )
        state = game.enter_room("combat", encounter="KAISER_CRAB_BOSS")

        assert state["decision"] == "combat_play"
        assert [enemy["name"] for enemy in state["enemies"]] == ["Crusher", "Rocket"]

        dismantle = next(card for card in state["hand"] if card["name"] == "Dismantle")
        crusher = next(enemy for enemy in state["enemies"] if enemy["name"] == "Crusher")
        state = game.act("play_card", card_index=dismantle["index"], target_index=crusher["index"])
        assert state["decision"] == "combat_play"

        twin_strike = next(card for card in state["hand"] if card["name"] == "Twin Strike")
        rocket = next(enemy for enemy in state["enemies"] if enemy["name"] == "Rocket")
        state = game.act("play_card", card_index=twin_strike["index"], target_index=rocket["index"])
        assert state["decision"] == "combat_play"

        state = game.act("end_turn")
        assert state["decision"] == "combat_play"
        assert state["player"]["hp"] > 0

    def test_doormaker_devoured_affliction_exports_localized_text(self, game):
        state = game.start(character="Defect", seed="doormaker-devoured-affliction")
        game.skip_neow(state)
        game.set_player(hp=999, max_hp=999)
        state = game.enter_room("combat", encounter="DOORMAKER_BOSS")

        state = game.act("end_turn")

        afflicted_cards = [
            card for card in state["hand"]
            if card.get("affliction_id") == "DEVOURED"
        ]
        assert afflicted_cards
        for card in afflicted_cards:
            assert card["affliction"] == "Devoured"
            assert card["affliction_description"] == "Add Exhaust to this card."
            assert card["affliction"] != "DEVOURED.title"
            assert card["affliction_description"] != "DEVOURED.description"

    def test_doormaker_weighted_affliction_exports_localized_text(self, game):
        state = game.start(character="Defect", seed="doormaker-weighted-affliction")
        game.skip_neow(state)
        game.set_player(hp=999, max_hp=999)
        state = game.enter_room("combat", encounter="DOORMAKER_BOSS")

        weighted_cards = []
        for _ in range(6):
            state = game.act("end_turn")
            weighted_cards = [
                card for card in state["hand"]
                if card.get("affliction_id") == "WEIGHTED"
            ]
            if weighted_cards:
                break

        assert weighted_cards
        for card in weighted_cards:
            assert "WEIGHTED.extraCardText" not in card["description"]
            assert card["affliction"] == "Weighted"
            assert card["affliction_description"] == "Lose [E] when this card is played."
            assert card["affliction"] != "WEIGHTED.title"
            assert card["affliction_description"] != "WEIGHTED.description"

    def test_act_three_queen_win_enters_architect_victory_room_first(self, tmp_path):
        game = Game()
        try:
            state = game.start(seed="queen-final-victory")
            state = game.skip_neow(state)

            save_path = tmp_path / "act_three.save"
            save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
            assert save_result["success"] is True
        finally:
            game.close()

        save_data = json.loads(save_path.read_text())
        save_data["current_act_index"] = 2
        save_data["visited_map_coords"] = [{"col": 3, "row": row} for row in range(15)]
        save_path.write_text(json.dumps(save_data))

        game = Game()
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["context"]["act"] == 3

        try:
            game.set_player(hp=9999, max_hp=9999, deck=["BLUDGEON"] * 50)
            state = game.enter_room("combat", encounter="QUEEN_BOSS")

            for _ in range(200):
                if state.get("decision") != "combat_play":
                    break

                playable = [card for card in state["hand"] if card.get("can_play")]
                if not playable:
                    state = game.act("end_turn")
                    continue

                card = playable[0]
                enemies = [enemy for enemy in state["enemies"] if enemy.get("hp", 0) > 0]
                assert enemies
                target = min(enemies, key=lambda enemy: enemy.get("hp", 0))
                state = game.act("play_card", card_index=card["index"], target_index=target["index"])

            assert state["decision"] == "event_choice"
            assert state["event_name"] == "The Architect"

            first_option = next(o for o in state["options"] if not o.get("is_locked"))
            assert first_option["text_key"] == "THE_ARCHITECT.dialogue.0"
            assert first_option["title"] == "Threaten"
            assert "arch demon" in first_option["description"]
            state = game.act("choose_option", option_index=first_option["index"])

            assert state["decision"] == "event_choice"
            assert state["event_name"] == "The Architect"
            proceed = next(o for o in state["options"] if o["text_key"] == "PROCEED")
            state = game.act("choose_option", option_index=proceed["index"])

            assert state["decision"] == "game_over"
            assert state["victory"] is True
            assert state["player"]["hp"] > 0
        finally:
            game.close()

    def test_decimillipede_reattach_headless_texture_does_not_force_game_over(self, game):
        state = game.start(seed="decimillipede-reattach-headless")
        game.skip_neow(state)
        game.set_player(
            hp=9999,
            max_hp=9999,
            deck=["BLUDGEON"] * 10,
            relics=["BAG_OF_MARBLES"],
        )
        state = game.enter_room("combat", encounter="DECIMILLIPEDE_ELITE")

        state = game.act("play_card", card_index=0, target_index=0)
        assert state["decision"] == "combat_play"
        assert len(state["enemies"]) == 2

        state = game.act("end_turn")
        assert state["decision"] == "combat_play"

        state = game.act("end_turn")
        assert state["decision"] == "combat_play"
        assert state["player"]["hp"] > 0

    def test_decimillipede_dead_segment_exports_pending_reattach(self, game):
        state = game.start(seed="decimillipede-reattach-export")
        game.skip_neow(state)
        game.set_player(
            hp=9999,
            max_hp=9999,
            deck=["BLUDGEON"] * 10,
            relics=["BAG_OF_MARBLES"],
        )
        state = game.enter_room("combat", encounter="DECIMILLIPEDE_ELITE")

        state = game.act("play_card", card_index=0, target_index=0)

        assert state["decision"] == "combat_play"
        assert len(state["enemies"]) == 2
        inactive = state.get("inactive_enemies")
        assert inactive
        assert inactive[0]["name"] == "Decimillipede"
        assert inactive[0]["alive"] is False
        assert inactive[0]["targetable"] is False
        assert any(power["name"] == "Reattach" for power in inactive[0]["powers"])

    def test_decimillipede_all_segments_defeated_resolves_rewards(self, game):
        state = game.start(seed="decimillipede-empty-after-whirlwind")
        game.skip_neow(state)
        game.set_player(
            hp=9999,
            max_hp=9999,
            deck=[
                "BLOODLETTING",
                "BLOODLETTING",
                "BLOODLETTING",
                "BLOODLETTING",
                "WHIRLWIND",
            ],
        )
        state = game.enter_room("combat", encounter="DECIMILLIPEDE_ELITE")

        while any(card["name"] == "Bloodletting" for card in state["hand"]):
            bloodletting = next(card for card in state["hand"] if card["name"] == "Bloodletting")
            state = game.act("play_card", card_index=bloodletting["index"])

        whirlwind = next(card for card in state["hand"] if card["name"] == "Whirlwind")
        state = game.act("play_card", card_index=whirlwind["index"])

        assert state["decision"] == "combat_reward"
        assert state.get("rewards")

    def test_decimillipede_reattach_vfx_does_not_log_headless_exception(self):
        result, outputs = run_headless_jsonl([
            {"cmd": "start_run", "character": "Ironclad", "seed": "decimillipede-stderr", "lang": "en"},
            {
                "cmd": "set_player",
                "hp": 9999,
                "max_hp": 9999,
                "deck": ["BLOODLETTING", "BLOODLETTING", "BLOODLETTING", "BLOODLETTING", "WHIRLWIND"],
            },
            {"cmd": "enter_room", "type": "combat", "encounter": "DECIMILLIPEDE_ELITE"},
            {"cmd": "action", "action": "play_card", "args": {"card_index": 0}},
            {"cmd": "action", "action": "play_card", "args": {"card_index": 0}},
            {"cmd": "action", "action": "play_card", "args": {"card_index": 0}},
            {"cmd": "action", "action": "play_card", "args": {"card_index": 0}},
            {"cmd": "action", "action": "play_card", "args": {"card_index": 0}},
            {"cmd": "quit"},
        ])

        assert result.returncode == 0
        assert outputs[-2]["decision"] == "combat_reward"
        assert "MissingMethodException" not in result.stderr
        assert "DoFadeOutOnAllSegments" not in result.stderr

    def test_slumbering_beetle_sleep_setup_does_not_log_headless_exception(self):
        result, outputs = run_headless_jsonl([
            {"cmd": "start_run", "character": "Ironclad", "seed": "slumbering-beetle-stderr", "lang": "en"},
            {"cmd": "set_player", "hp": 9999, "max_hp": 9999},
            {"cmd": "enter_room", "type": "combat", "encounter": "SLUMBERING_BEETLE_NORMAL"},
            {"cmd": "quit"},
        ])

        assert result.returncode == 0
        assert outputs[-2]["type"] == "decision"
        assert "NullReferenceException" not in result.stderr
        assert "SlumberingBeetle.AfterAddedToRoom" not in result.stderr

    def test_lagavulin_matriarch_sleep_setup_does_not_log_headless_exception(self):
        result, outputs = run_headless_jsonl([
            {"cmd": "start_run", "character": "Ironclad", "seed": "lagavulin-matriarch-stderr", "lang": "en"},
            {"cmd": "set_player", "hp": 9999, "max_hp": 9999},
            {"cmd": "enter_room", "type": "combat", "encounter": "LAGAVULIN_MATRIARCH_BOSS"},
            {"cmd": "quit"},
        ])

        assert result.returncode == 0
        assert outputs[-2]["type"] == "decision"
        assert outputs[-2]["decision"] == "combat_play"
        assert "NullReferenceException" not in result.stderr
        assert "LagavulinMatriarch.AfterAddedToRoom" not in result.stderr

    def test_rolling_boulder_turn_start_does_not_log_headless_connect_exception(self):
        session = HeadlessSession()
        stderr = ""
        try:
            state = session.send({
                "cmd": "start_run",
                "character": "Ironclad",
                "seed": "rolling-boulder-connect-stderr",
                "lang": "en",
            })
            state = session.skip_neow(state)
            state = session.send({
                "cmd": "set_player",
                "hp": 999,
                "max_hp": 999,
                "deck": [
                    "ROLLING_BOULDER",
                    "STRIKE_IRONCLAD",
                    "DEFEND_IRONCLAD",
                    "STRIKE_IRONCLAD",
                    "DEFEND_IRONCLAD",
                ],
            })
            state = session.send({"cmd": "enter_room", "type": "combat", "encounter": "SHRINKER_BEETLE_WEAK"})

            rolling_boulder = next(card for card in state["hand"] if card["name"] == "Rolling Boulder")
            state = session.send({
                "cmd": "action",
                "action": "play_card",
                "args": {"card_index": rolling_boulder["index"]},
            })
            state = session.send({"cmd": "action", "action": "end_turn"})
        finally:
            stderr = session.close()

        assert state["decision"] == "combat_play"
        assert state["player"]["hp"] > 0
        assert "MissingMethodException" not in stderr
        assert "GodotObject.Connect" not in stderr
        assert "RollingBoulderPower.AfterPlayerTurnStart" not in stderr

    def test_test_subject_respawn_and_burning_growl_presentation_do_not_log_headless_exception(self):
        session = HeadlessSession()
        stderr = ""
        try:
            state = session.send({
                "cmd": "start_run",
                "character": "Ironclad",
                "seed": "test-subject-set-color-stderr",
                "lang": "en",
            })
            state = session.skip_neow(state)
            state = session.send({
                "cmd": "set_player",
                "hp": 999,
                "max_hp": 999,
                "relics": ["BURNING_BLOOD", "LANTERN"],
                "deck": [
                    "BLOODLETTING",
                    "BLOODLETTING",
                    "PERFECTED_STRIKE",
                    "PERFECTED_STRIKE",
                    "PERFECTED_STRIKE",
                    *(["STRIKE_IRONCLAD"] * 30),
                ],
            })
            state = session.send({"cmd": "enter_room", "type": "combat", "encounter": "TEST_SUBJECT_BOSS"})

            reached_respawn = False
            resolved_burning_growl = False
            for _ in range(200):
                enemies = state.get("enemies", [])
                if any(enemy.get("max_hp", 0) >= 300 for enemy in enemies):
                    reached_respawn = True
                if enemies and enemies[0].get("move_id") == "BURNING_GROWL_MOVE":
                    state = session.send({"cmd": "action", "action": "end_turn"})
                    resolved_burning_growl = True
                    break

                if state.get("decision") != "combat_play":
                    state = session.send({"cmd": "action", "action": "proceed"})
                    continue

                playable = [
                    card for card in state["hand"]
                    if card.get("can_play") and card_energy_cost(card) <= state.get("energy", 0)
                ]
                if not playable:
                    state = session.send({"cmd": "action", "action": "end_turn"})
                    continue

                playable.sort(key=lambda card: (
                    0 if card["name"] == "Bloodletting" else
                    1 if card["name"] == "Perfected Strike" else
                    2 if card["type"] == "Attack" else
                    3,
                    card_energy_cost(card),
                ))
                card = playable[0]
                args = {"card_index": card["index"]}
                if card.get("target_type") == "AnyEnemy":
                    args["target_index"] = 0
                state = session.send({"cmd": "action", "action": "play_card", "args": args})
        finally:
            stderr = session.close()

        assert reached_respawn
        assert resolved_burning_growl
        assert "MissingMethodException" not in stderr
        assert "NullReferenceException" not in stderr
        assert "SetSelfModulate" not in stderr
        assert "TestSubject.SetColor" not in stderr
