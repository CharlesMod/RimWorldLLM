import asyncio
import random

import httpx
import pytest

from hermesvoice_agent.core.bridge import BridgeClient
from hermesvoice_agent.core.config import Config
from hermesvoice_agent.core.director import Director
from hermesvoice_agent.core.fakes import FakeLLM
from hermesvoice_agent.games.rimworld import RimWorldAdapter
from hermesvoice_agent.games.rimworld.fakes import FakeBridge


CLASSIC_CREATIVE = dict(aim_ground=False, creative_ground_for=(), creative_plan_for=(), creative_best_of_for=())


def run(coro):
    return asyncio.run(coro)


class World:
    def __init__(self, tmp_path):
        # the creative-round defaults (aim tension, per-kind plan/best-of/grounding) are off here so the older tests keep
        # pinning the one-call paths; tests/test_creative*.py cover the kept defaults
        self.cfg = Config(data_dir=tmp_path, thought_enabled=False, bridge_url="http://fake", bridge_token="tok", turn_gap_ticks=0,
                          turn_gap_floor_real_s=0.0, **CLASSIC_CREATIVE)
        self.fb = FakeBridge("tok")
        self.fb.add_pawn(1, "Ana", traits=[{"def": "Kind", "label": "kind", "degree": 0}])
        self.fb.add_pawn(2, "Bram", traits=[{"def": "Abrasive", "label": "abrasive", "degree": 0}])
        self.fb.add_pawn(3, "Cy")
        self.real = 0.0
        self.adapter = RimWorldAdapter()
        self.bridge = BridgeClient(httpx.AsyncClient(transport=self.fb.transport), "http://fake", "tok", self.adapter.protocol)
        self.llm = FakeLLM()
        self.d = Director(self.cfg, self.bridge, self.llm, self.adapter, clock=lambda: self.real, rng=random.Random(1))

    async def go(self):
        await self.d.start()
        return self


@pytest.fixture
def world(tmp_path):
    return World(tmp_path)


@pytest.fixture(autouse=True)
def _adult_setting_default():
    from hermesvoice_agent.core import adult
    adult.set_enabled(False)
    yield
    adult.set_enabled(False)
