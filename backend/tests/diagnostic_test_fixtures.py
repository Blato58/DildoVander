"""Synthetic capability data for offline diagnostic contract tests."""

def capabilities(ready=True):
    engines = []
    for engine, model, speed in (
        ("gpt-live", "gpt-live-1", {"supported": False, "min": 1, "max": 1, "default": 1}),
        ("realtime", "gpt-realtime-2.1", {"supported": True, "min": 0.8, "max": 1.2, "default": 1}),
    ):
        engines.append({"engine": engine, "model": model, "ready": ready,
            "unavailableReason": None if ready else "provider_credential_missing",
            "voices": ["cedar", "marin", "coral"], "speed": speed,
            "inputModes": ["audio"], "cameraSources": ["phone", "suitcase"]})
    return {"schemaVersion": 1, "primaryEngine": "gpt-live", "engines": engines}
