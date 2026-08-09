import re
from html import unescape
from xml.sax.saxutils import escape

_XML_ELEMENT_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9._-]*$")


def escape_xml_text(raw: str) -> str:
    return escape(unescape(raw), {'"': "&quot;", "'": "&apos;"})


def escape_cdata(raw: str) -> str:
    return unescape(raw).replace("]]>", "]]]]><![CDATA[>")


def is_valid_xml_element_name(name: str) -> bool:
    """Return whether an ASCII provider tag is a valid XML element name."""
    return _XML_ELEMENT_NAME.fullmatch(name) is not None
