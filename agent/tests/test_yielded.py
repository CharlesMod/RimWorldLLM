import logging

import httpx

from conftest import run
from hermesvoice_agent.core.config import LLMConfig
from hermesvoice_agent.core.llm import HttpLLM, LLMAway, LLMError
from hermesvoice_agent.core.toolloop.backend import BackendAway, BackendError, OpenAIBackend

YIELDED = {"error": {"code": "yielded", "type": "unavailable_error", "message": "Paused while RimWorld runs"}}
LOADING = {"error": {"code": 503, "type": "unavailable_error", "message": "Loading model"}}
OKBODY = {"choices": [{"message": {"content": "Hi there"}}]}


class Server:
    def __init__(self, status=503, body=YIELDED, retry="7"):
        self.status, self.body, self.retry, self.n = status, body, retry, 0

    def __call__(self, req):
        self.n += 1
        if self.status == 200:
            return httpx.Response(200, json=OKBODY)
        h = {"Retry-After": self.retry} if self.retry is not None else {}
        return httpx.Response(self.status, json=self.body, headers=h)


def llm(srv):
    return HttpLLM(LLMConfig(base_url="http://h:1", engine=False),
                   client=httpx.AsyncClient(transport=httpx.MockTransport(srv)))


def call(c):
    return run(c.complete([("user", "hi")], slot=0))


def test_yielded_raises_away_with_retry_after():
    s = Server()
    try:
        call(llm(s))
        assert False
    except LLMAway as e:
        assert e.retry_after == 7 and isinstance(e, LLMError) and "RimWorld" in e.message


def test_retry_after_capped_and_defaulted():
    for hdr, want in (("600", 30), (None, 5), ("junk", 5)):
        try:
            call(llm(Server(retry=hdr)))
        except LLMAway as e:
            assert e.retry_after == want


def test_backoff_skips_network_and_logs_once(caplog):
    s = Server()
    c = llm(s)
    with caplog.at_level(logging.INFO, logger="hermesvoice"):
        for _ in range(5):
            try:
                call(c)
            except LLMAway:
                pass
    assert s.n == 1
    assert len([r for r in caplog.records if "paused" in r.getMessage()]) == 1
    assert len(c.calllog.recent()) <= 1 if hasattr(c.calllog, "recent") else True


def test_comes_back_after_backoff(caplog):
    s = Server()
    c = llm(s)
    with caplog.at_level(logging.INFO, logger="hermesvoice"):
        try:
            call(c)
        except LLMAway:
            pass
        c.gate.until = 0.0001          # the window has passed
        s.status = 200
        assert call(c).text == "Hi there"
    assert any("back" in r.getMessage() for r in caplog.records)


def test_plain_503_is_plain_llmerror():
    s = Server(body=LOADING)
    for _ in range(2):
        try:
            call(llm(s))
        except LLMAway:
            assert False
        except LLMError as e:
            assert e.status == 503


def backend(srv):
    return OpenAIBackend("http://h:1", engine=False, client=httpx.AsyncClient(transport=httpx.MockTransport(srv)))


def test_backend_yielded_away_and_backoff(caplog):
    s = Server()
    b = backend(s)
    with caplog.at_level(logging.INFO, logger="hermesvoice"):
        for _ in range(3):
            try:
                run(b.chat([{"role": "user", "content": "x"}]))
                assert False
            except BackendAway as e:
                assert 6 < e.retry_after <= 7 and isinstance(e, BackendError)
    assert s.n == 1
    assert len([r for r in caplog.records if "paused" in r.getMessage()]) == 1


def test_backend_plain_503_stays_backend_error():
    try:
        run(backend(Server(body=LOADING)).chat([{"role": "user", "content": "x"}]))
    except BackendAway:
        assert False
    except BackendError:
        pass
