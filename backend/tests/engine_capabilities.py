"""Validate the versioned two-engine diagnostic contract without inference."""

MODELS = {"gpt-live": "gpt-live-1", "realtime": "gpt-realtime-2.1"}


def require(condition, label):
    if not condition:
        raise AssertionError(label)


def validate_engine_capabilities(value, *, require_ready=False):
    require(isinstance(value, dict) and type(value.get("schemaVersion")) is int and value.get("schemaVersion") == 1
            and value.get("primaryEngine") == "gpt-live", "engine_contract_version")
    engines = value.get("engines")
    require(isinstance(engines, list) and len(engines) == 2, "engine_inventory")
    require(all(isinstance(engine, dict) for engine in engines), "engine_inventory")
    require(all(isinstance(engine.get("engine"), str) for engine in engines), "engine_inventory")
    require({engine.get("engine") for engine in engines} == set(MODELS), "engine_inventory")
    for engine in engines:
        name = engine["engine"]
        require(engine.get("model") == MODELS[name], "engine_model")
        ready = engine.get("ready")
        reason = engine.get("unavailableReason")
        require(type(ready) is bool and ((ready and reason is None)
                or (not ready and reason == "provider_credential_missing")), "engine_readiness")
        if require_ready:
            require(ready, "engine_not_configured")
        require(engine.get("voices") == ["cedar", "marin", "coral"], "engine_voices")
        speed = engine.get("speed")
        expected_speed = {"supported": False, "min": 1, "max": 1, "default": 1} if name == "gpt-live" else {
            "supported": True, "min": 0.8, "max": 1.2, "default": 1}
        require(isinstance(speed, dict) and type(speed.get("supported")) is bool
                and all(type(speed.get(field)) in (int, float) for field in ("min", "max", "default"))
                and speed == expected_speed, "engine_speed")
        require(engine.get("inputModes") == ["audio"], "engine_input_modes")
        require(engine.get("cameraSources") == ["phone", "suitcase"], "engine_camera_contracts")
    return engines
