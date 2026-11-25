"""Game-agnostic tool harness: scales, tool specs, a grammar built from them, and a loop that cannot go silent.

See docs/TOOL-HARNESS.md.
"""
from .backend import Backend, BackendError, OpenAIBackend, Reply
from .harness import Capabilities, ToolHarness, choose_protocol, probe
from .loop import Action, Executed, LoopResult, StepTrace, run_loop
from .parse import parse_text
from .scale import CHANGE, IMPORTANCE, Scale
from .spec import Param, ToolSpec, build_grammar, has_raw_syntax

__all__ = ["Action", "Backend", "BackendError", "CHANGE", "Capabilities", "Executed", "IMPORTANCE", "LoopResult", "OpenAIBackend",
           "Param", "Reply", "Scale", "StepTrace", "ToolHarness", "ToolSpec", "build_grammar", "choose_protocol",
           "has_raw_syntax", "parse_text", "probe", "run_loop"]
