from lxml import etree

from mdcx.crawlers.javbus import (
    clean_title_number,
    getActor,
    getActorPhoto,
    getCover,
    getExtraFanart,
    getTag,
    getWebNumber,
    restore_lookup_number,
    rewrite_lookup_number,
)


def parse_html(source: str):
    return etree.fromstring(source, etree.HTMLParser())


def test_javbus_parses_current_actor_and_media_markup():
    html = parse_html(
        """
        <html><head><meta property="og:image" content="/pics/cover/SNOS-157_b.jpg"></head>
        <body><div class="movie row">
          <p><span class="header">識別碼:</span> SNOS-157</p>
          <a href="/star/a"><img title="白石透羽" src="/pics/actress/a.jpg">白石透羽</a>
          <a href="/star/a">白石透羽</a>
          <span class="genre"><a href="/genre/1">單體作品</a></span>
          <div id="sample-waterfall"><a href="/pics/sample/1.jpg">sample</a></div>
        </div></body></html>
        """
    )

    assert getWebNumber(html, "fallback") == "SNOS-157"
    assert getActor(html) == "白石透羽"
    assert getActorPhoto(html, "https://www.javbus.com") == {
        "白石透羽": "https://www.javbus.com/pics/actress/a.jpg"
    }
    assert getCover(html, "https://www.javbus.com") == "https://www.javbus.com/pics/cover/SNOS-157_b.jpg"
    assert getExtraFanart(html, "https://www.javbus.com") == ["https://www.javbus.com/pics/sample/1.jpg"]
    assert getTag(html) == "單體作品"


def test_javboss_number_rewrites_are_reversible():
    for source, requested in (
        ("GANA-1234", "200GANA-1234"),
        ("MIUM-1234", "300MIUM-1234"),
        ("LUXU-1234", "259LUXU-1234"),
    ):
        rewritten, rule = rewrite_lookup_number(source)
        assert rewritten == requested
        assert restore_lookup_number(rewritten, rule) == source

    assert rewrite_lookup_number("SNOS-157") == ("SNOS-157", None)


def test_title_only_removes_a_leading_number():
    assert clean_title_number("SNOS-157 Sample SNOS-157", "SNOS-157") == "Sample SNOS-157"

