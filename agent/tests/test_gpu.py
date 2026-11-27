"""Integration against a live llama-server. Skipped unless RIMWORLDLLM_TEST_LLM_URL is set
(and run with -m gpu). Never run in CI or while the card is busy."""
import os

import pytest

from conftest import run
from rimworldllm_agent.core.config import LLMConfig
from rimworldllm_agent.core import endpoint_test as ept
from rimworldllm_agent.core.llm import HttpLLM

URL = os.environ.get("RIMWORLDLLM_TEST_LLM_URL")
pytestmark = [pytest.mark.gpu, pytest.mark.skipif(not URL, reason="RIMWORLDLLM_TEST_LLM_URL not set")]


def test_live_endpoint_and_line():
    cfg = LLMConfig(base_url=URL, mode=os.environ.get("RIMWORLDLLM_TEST_LLM_MODE", "native"))
    assert run(ept.test_endpoint(cfg)).status in ("green", "amber")
    c = run(HttpLLM(cfg).complete([("user", "Say one short line.")], slot=1, max_tokens=24))
    assert c.text.strip() and c.stats.prompt_n >= 0
