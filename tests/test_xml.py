from xml.etree import ElementTree

from mdcx.utils.xml import escape_cdata, escape_xml_text


def test_xml_text_is_not_double_escaped():
    value = 'A &amp; B < C "quoted"'
    xml = f"<title>{escape_xml_text(value)}</title>"

    assert ElementTree.fromstring(xml).text == 'A & B < C "quoted"'


def test_cdata_preserves_text_and_handles_cdata_terminator():
    value = "A &amp; B ]]> C"
    xml = f"<plot><![CDATA[{escape_cdata(value)}]]></plot>"

    assert ElementTree.fromstring(xml).text == "A & B ]]> C"


def test_jellyfin_country_tag_name():
    assert ElementTree.fromstring("<movie><country>JP</country></movie>").findtext("country") == "JP"
