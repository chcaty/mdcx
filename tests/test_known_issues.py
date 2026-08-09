import pytest

from mdcx.config.enums import Language, Website
from mdcx.config.models import DISABLED_CRAWLER_REASONS, Config
from mdcx.config.v1 import ConfigV1
from mdcx.gen.field_enums import CrawlerResultFields
from mdcx.manual import ManualConfig
from mdcx.models.types import CrawlerInput
from mdcx.number import get_number_letters, normalize_number, resolve_scraped_number


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


def test_broken_crawler_reasons_are_recorded():
    assert set(DISABLED_CRAWLER_REASONS) == {Website.AVSOX, Website.CABLEAV, Website.XCITY}
    assert all(DISABLED_CRAWLER_REASONS.values())


def test_javboss_style_defaults_prefer_javbus():
    config = Config()

    assert config.website_single == Website.JAVBUS
    for field in (
        CrawlerResultFields.TITLE,
        CrawlerResultFields.ACTORS,
        CrawlerResultFields.ALL_ACTORS,
        CrawlerResultFields.TAGS,
        CrawlerResultFields.THUMB,
        CrawlerResultFields.POSTER,
    ):
        assert config.get_field_config(field).site_prority[0] == Website.JAVBUS


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


def test_scraped_number_resolution_rejects_missing_or_mismatched_values():
    assert resolve_scraped_number("", "SNOS-157") == "SNOS-157"
    assert resolve_scraped_number("snos 157", "SNOS-157") == "SNOS-157"
    with pytest.raises(ValueError, match="did not provide"):
        resolve_scraped_number("SNOS-157", "")
    with pytest.raises(ValueError, match="Number mismatch"):
        resolve_scraped_number("SNOS-157", "SNOS-158")


def test_crawler_input_is_copied_for_each_site():
    crawler_input = CrawlerInput(
        appoint_number="",
        appoint_url="",
        file_path=None,
        mosaic="",
        number="259LUXU-1111",
        short_number="LUXU-1111",
        language=Language.UNDEFINED,
        org_language=Language.UNDEFINED,
    )

    assert crawler_input.for_site(Website.JAVDB).number == "LUXU-1111"
    assert crawler_input.for_site(Website.MGSTAGE).number == "259LUXU-1111"
    assert crawler_input.for_site(Website.AVSEX).number == "259LUXU-1111"
    assert crawler_input.number == "259LUXU-1111"


def test_existing_config_migrates_disabled_sites_and_old_domains():
    data = {
        "website_youma": ["official", "cableav", "avsox", "xcity"],
        "website_wuma": ["javdb", "avsox"],
        "website_fc2": ["fc2", "avsox"],
        "website_single": "xcity",
        "site_configs": {
            "iqqtv": {"custom_url": "https://iqq5.xyz"},
            "javday": {"custom_url": "https://javday.tv"},
        },
        "field_configs": {
            "title": {"site_prority": ["xcity", "official"]},
        },
    }

    changes = Config.update(data)
    config = Config.model_validate(data)

    assert changes
    assert config.website_youma == {Website.OFFICIAL}
    assert config.website_wuma == {Website.JAVDB}
    assert config.website_fc2 == {Website.FC2}
    assert config.website_single == Website.AIRAV_CC
    assert config.get_site_url(Website.IQQTV) == "https://iqqk4.quest"
    assert config.get_site_url(Website.JAVDAY) == "https://javday.app"
    assert config.field_configs[CrawlerResultFields.TITLE].site_prority == [Website.OFFICIAL]


def test_legacy_field_conversion_uses_domestic_sites():
    data = {
        "website_youma": ["javbus"],
        "website_guochan": ["madouqu"],
        "website_set": [],
        "whole_fields": ["title"],
        "none_fields": [],
    }

    Config._convert_field_configs(data)
    assert Website.MADOUQU in data["field_configs"][CrawlerResultFields.TITLE].site_prority
