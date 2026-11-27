# csmind part "stream": opt-in streaming (NEW behaviour, not in the Python)

Added on top of the faithful port. Non-streaming stays the default and the fallback; its request bytes are unchanged
(the golden tests still pass untouched).

Files: `Llm/LlmStream.cs` (events, SSE parser, chunk reader, error mapping, `StreamSender`), `Llm/FinalText.cs`,
edits in `Llm/LlmClient.cs` (HttpLlm, LlmError, HttpUtil), `Llm/CallLog.cs` (request_id), `ToolLoop/Backend.cs`.
Tests: `Mod.Tests/Mind/StreamTests.cs`, `FinalTextTests.cs` (92 tests).

## Wire
- Sink given, openai mode only (native mode never streams): same body as non-stream, then `"stream":true`,
  `"stream_options":{"include_usage":true}`, and `"return_progress":true` only when the endpoint is detected as easyEngine.
- Every LLM request (stream or not, both modes, OpenAIBackend too) sends `X-Request-Id: rwl-<32 hex>` (36 bytes). Goldens compare
  named headers only, so none needed changes. `CallStats.RequestId` is appended last in the calls.jsonl row as `request_id`
  (absent when null, so rows from other clients are unchanged).
- Stream requests also send `Accept: text/event-stream`.
- Timeout: for a streamed call `TimeoutS` is an idle time (to the response headers, then between reads), not a total.
- A 200 with `Content-Type: application/json` (server ignored `stream`) is read as a whole chat completion.

## Errors
- Pre-stream: `{"error":"text"}` and `{"error":{code,type,message}}` both map; 503 code "yielded" -> `LlmAway` (Retry-After, clamped 0..30,
  default 5, gate engaged); 503 numeric code, 401 (WWW-Authenticate kept), 403, 502, other -> `LlmError("HTTP <status>")`
  with `Detail`, `Code`, `ErrorType`, `RetryAfterS` (Retry-After seconds) filled; a string code in an object ("upstream_error") becomes the Cause.
- Mid-stream `data: {"error":{...}}` is terminal even after a 200: "yielded" -> `LlmAway` (uses `retry` seconds if given, else 5);
  other string codes -> `LlmError(code, status)` (status from the object's `status`); numeric code -> `LlmError("HTTP <code>", code)`.
- Close without `[DONE]` and without an error -> `LlmError("stream ended without [DONE]")`. Bad JSON chunk -> `LlmError("bad stream chunk")`.
- Transport failure -> `LlmError("server unreachable (ReadTimeout|ConnectError|ReadError)")` as in non-stream.
- Cancellation: `OperationCanceledException` out of the call (not an LlmError); call-log row error "cancelled"; the sink gets Error("cancelled").
- One call-log row per call on every path (success or failure), as non-stream; none when the gate refuses before sending.
- A sink exception is swallowed.
