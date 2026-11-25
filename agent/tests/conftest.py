import asyncio
import random

import httpx
import pytest

from hermesvoice_agent.bridge import BridgeClient
from hermesvoice_agent.config import Config
from hermesvoice_agent.director import Director
from hermesvoice_agent.fakes import FakeBridge, FakeLLM


def run(coro):
    return asyncio.run(coro)


class World:
    def __init__(self, tmp_path):
        self.cfg = Config(data_dir=tmp_path, bridge_url="http://fake", bridge_token="tok")
        self.fb = FakeBridge("tok")
        self.fb.add_pawn(1, "Ana", traits=[{"def": "Kind", "label": "kind", "degree": 0}])
        self.fb.add_pawn(2, "Bram", traits=[{"def": "Abrasive", "label": "abrasive", "degree": 0}])
        self.fb.add_pawn(3, "Cy")
        self.real = 0.0
        self.bridge = BridgeClient(httpx.AsyncClient(transport=self.fb.transport), "http://fake", "tok")
        self.llm = FakeLLM()
        self.d = Director(self.cfg, self.bridge, self.llm, clock=lambda: self.real, rng=random.Random(1))

    async def go(self):
        await self.d.start()
        return self


@pytest.fixture
def world(tmp_path):
    return World(tmp_path)
