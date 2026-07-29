from mdcx.config.enums import Website
from mdcx.config.models import Config
from mdcx.config.v1 import ConfigV1
from mdcx.manual import ManualConfig
from mdcx.number import get_number_letters, normalize_number


def test_snos_is_mapped_to_s1_official_site():
    prefix = get_number_letters("SNOS-157")
    configured_prefixes = ManualConfig.OFFICIAL["https://s1s1s1.com"].upper().split("|")

    assert prefix == "SNOS"
    assert prefix in configured_prefixes


def test_broken_crawlers_are_not_enabled_by_default():
    config = Config()

    assert Website.CABLEAV not in config.website_youma
    assert Website.XCITY not in config.website_youma
    assert Website.AVSOX not in config.website_youma
    assert Website.AVSOX not in config.website_wuma
    assert Website.AVSOX not in config.website_fc2


def test_broken_crawlers_are_not_in_legacy_defaults():
    config = ConfigV1()

    assert "cableav" not in config.website_youma
    assert "xcity" not in config.website_youma
    assert "avsox" not in config.website_youma
    assert "avsox" not in config.website_wuma
    assert "avsox" not in config.website_fc2


def test_number_normalization_ignores_common_separators():
    assert normalize_number("snos-157") == "SNOS157"
    assert normalize_number("SNOS 157") == "SNOS157"
