from html import unescape
from xml.sax.saxutils import escape


def escape_xml_text(raw: str) -> str:
    return escape(unescape(raw), {'"': "&quot;", "'": "&apos;"})


def escape_cdata(raw: str) -> str:
    return unescape(raw).replace("]]>", "]]]]><![CDATA[>")
