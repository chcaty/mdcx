import json
from pathlib import Path

import mdcx.config.manager as manager_module
from mdcx.config.enums import Website
from mdcx.config.manager import ConfigManager
from mdcx.config.models import Config


def test_config_migrations_do_not_block_switching(tmp_path: Path, monkeypatch):
    monkeypatch.setattr(manager_module, "MAIN_PATH", tmp_path)
    monkeypatch.setattr(manager_module, "MARK_FILE", tmp_path / "MDCx.config")
    config_path = tmp_path / "legacy.json"
    data = json.loads(Config().model_dump_json())
    data["website_youma"] = ["javbus", "avsox", "xcity"]
    config_path.write_text(json.dumps(data), encoding="utf-8")

    config_manager = ConfigManager()
    config_manager.path = config_path
    errors = config_manager.load()

    assert errors == []
    assert config_manager.file == "legacy.json"
    assert config_manager.last_migrations
    assert config_manager.config.website_youma == {Website.JAVBUS}
