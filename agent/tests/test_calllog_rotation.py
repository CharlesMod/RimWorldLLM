"""calls.jsonl rotates by size: calls.jsonl -> .1 -> .2 -> .3, the oldest dropped."""
import json

from rimworldllm_agent.core.calllog import CallLog, CallStats


def rows(p):
    return [json.loads(x)["error"] for x in p.read_text(encoding="utf-8").splitlines()]


def test_rotates_and_keeps_three(tmp_path):
    p = tmp_path / "calls.jsonl"
    log = CallLog(p, max_bytes=1500, keep=3)
    for i in range(40):
        log.record(CallStats(kind="line", error=f"r{i}"))
    names = sorted(x.name for x in tmp_path.iterdir())
    assert names == ["calls.jsonl", "calls.jsonl.1", "calls.jsonl.2", "calls.jsonl.3"]
    for f in tmp_path.iterdir():
        assert f.stat().st_size <= 1500
    # newest rows in the live file, older ones in higher numbers, none repeated
    seq = rows(tmp_path / "calls.jsonl.3") + rows(tmp_path / "calls.jsonl.2") + rows(tmp_path / "calls.jsonl.1") + rows(p)
    nums = [int(x[1:]) for x in seq]
    assert nums == sorted(nums) and nums[-1] == 39 and len(set(nums)) == len(nums)
    assert nums[0] > 0                      # the oldest were dropped


def test_no_rotation_below_limit_and_none_path(tmp_path):
    p = tmp_path / "calls.jsonl"
    log = CallLog(p)
    for i in range(5):
        log.record(CallStats(error=f"r{i}"))
    assert [x.name for x in tmp_path.iterdir()] == ["calls.jsonl"] and len(rows(p)) == 5
    CallLog(None).record(CallStats())       # memory only, no file
