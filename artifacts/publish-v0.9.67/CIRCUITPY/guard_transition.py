# guard_transition.py - portable Guard + Executor transition policy
# The Pico runtime uses this module after the bundle is copied to CIRCUITPY.
# It is deliberately independent from Classroom Studio and desktop authoring tools.

PROFILE_TO_ROUTE = {
    "desktop": "desktop_steps.txt",
    "login-or-dc": "login_or_dc_steps.txt",
    "dc": "dc_steps.txt",
    "character-dashboard": "character_dashboard_steps.txt",
    "entering-game-loading": "entering_game_loading_steps.txt",
    "game": "game_steps.txt",
    "targeted": "targeted_steps.txt",
}

STAGES = {
    "desktop": 1,
    "login-or-dc": 2,
    "character-dashboard": 3,
    "entering-game-loading": 4,
    "game": 5,
}


class GuardTransition:
    def __init__(self):
        self.stage = None
        self.last_stable = None
        self.targeted_active = False

    def reset(self):
        self.stage = None
        self.last_stable = None
        self.targeted_active = False

    def observe_unknown(self):
        # Unknown/ambiguous/no-match is a state change for one-shot purposes,
        # but it does not invent a new progression stage.
        self.last_stable = None

    def observe(self, profile_id):
        if profile_id not in PROFILE_TO_ROUTE:
            return self._deny("unknown-profile")
        if profile_id == self.last_stable:
            return self._deny("duplicate-stable-state")
        self.last_stable = profile_id

        if profile_id == "desktop":
            return self._desktop()
        if profile_id == "login-or-dc":
            return self._login_or_dc()
        if profile_id == "character-dashboard":
            return self._linear("character-dashboard", 2, 3)
        if profile_id == "entering-game-loading":
            return self._linear("entering-game-loading", 3, 4)
        if profile_id == "game":
            return self._game()
        if profile_id == "targeted":
            return self._targeted()
        return self._deny("unsupported-profile")

    def _desktop(self):
        if self.stage is not None:
            return self._deny("desktop-only-valid-at-start")
        self.stage = 1
        return self._execute("desktop", "normal", 1, 1, "initial-stage-1")

    def _login_or_dc(self):
        if self.targeted_active or self.stage in (3, 4, 5):
            # DC has priority over the Targeted route and resets ordered progress
            # to stage 2. Keep the side-state marker so the eventual fresh Game
            # observation returns from Targeted without executing Game a second time.
            self.stage = 2
            return self._execute("login-or-dc", "dc", 2, 2,
                                 "dc-fallback-to-stage-2", "dc_steps.txt")
        if self.stage == 1:
            self.stage = 2
            return self._execute("login-or-dc", "login", 2, 2,
                                 "stage-1-to-stage-2")
        return self._deny("login-or-dc-not-expected")

    def _linear(self, profile_id, expected, next_stage):
        if self.stage != expected:
            return self._deny("unexpected-ordered-stage")
        self.stage = next_stage
        return self._execute(profile_id, "normal", next_stage, next_stage,
                             "stage-%d-to-stage-%d" % (expected, next_stage))

    def _game(self):
        if self.targeted_active:
            self.targeted_active = False
            return {
                "execute": False,
                "profile": "game",
                "route": PROFILE_TO_ROUTE["game"],
                "context": "return-from-targeted",
                "stage": 5,
                "next_stage": 5,
                "reason": "targeted-returned-to-game",
            }
        if self.stage != 4:
            return self._deny("game-not-expected")
        self.stage = 5
        return self._execute("game", "normal", 5, 5, "stage-4-to-stage-5")

    def _targeted(self):
        if self.stage != 5 or self.targeted_active:
            return self._deny("targeted-only-from-game")
        self.targeted_active = True
        return self._execute("targeted", "targeted", 5, 5,
                             "game-to-targeted-side-state")

    def _execute(self, profile_id, context, stage, next_stage, reason, route_override=None):
        return {
            "execute": True,
            "profile": profile_id,
            "route": route_override or PROFILE_TO_ROUTE[profile_id],
            "context": context,
            "stage": stage,
            "next_stage": next_stage,
            "reason": reason,
        }

    def _deny(self, reason):
        return {
            "execute": False,
            "profile": None,
            "route": None,
            "context": None,
            "stage": self.stage,
            "next_stage": self.stage,
            "reason": reason,
        }
