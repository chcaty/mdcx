#!/usr/bin/env python3
import asyncio
import re
import time
from urllib.parse import urljoin

from lxml import etree

from ..config.enums import Website
from ..config.manager import manager
from ..models.log_buffer import LogBuffer

_JAVBUS_REQUEST_INTERVAL = 0.5
_javbus_request_lock = asyncio.Lock()
_javbus_next_request = 0.0
_CODE_REWRITES = {
    "GANA": "200GANA",
    "MIUM": "300MIUM",
    "LUXU": "259LUXU",
}


def rewrite_lookup_number(number: str) -> tuple[str, tuple[str, str] | None]:
    number = number.strip()
    upper = number.upper()
    for input_prefix, request_prefix in _CODE_REWRITES.items():
        if re.match(rf"^{input_prefix}(?=[\-_ ]?\d)", upper):
            return request_prefix + number[len(input_prefix) :], (input_prefix, request_prefix)
    return number, None


def restore_lookup_number(value: str, rewrite: tuple[str, str] | None) -> str:
    value = value.strip()
    if rewrite is None:
        return value
    input_prefix, request_prefix = rewrite
    if value.upper().startswith(request_prefix):
        return input_prefix + value[len(request_prefix) :]
    return value


async def get_text_rate_limited(url: str, headers: dict[str, str]):
    global _javbus_next_request
    async with _javbus_request_lock:
        loop = asyncio.get_running_loop()
        wait = _javbus_next_request - loop.time()
        if wait > 0:
            await asyncio.sleep(wait)
        _javbus_next_request = loop.time() + _JAVBUS_REQUEST_INTERVAL
    return await manager.computed.async_client.get_text(url, headers=headers)


def get_title(html):
    result = html.xpath("string(//h3[1])").strip()
    if not result:
        result = html.xpath("string(//title[1])").strip()
    return re.sub(r"\s*-\s*JavBus\s*$", "", result, flags=re.IGNORECASE).strip()


def getWebNumber(html, number):
    for label in html.xpath("//span"):
        label_text = "".join(label.itertext()).strip().lower().rstrip(":：")
        if label_text not in {"識別碼", "识别码", "品番", "id"}:
            continue
        sibling = label.getnext()
        if sibling is not None:
            value = "".join(sibling.itertext()).strip()
            if value:
                return value
        parent_text = "".join(label.getparent().itertext()).strip() if label.getparent() is not None else ""
        value = parent_text.removeprefix("".join(label.itertext()).strip()).strip(" :：")
        if value:
            return value
    return number


def clean_title_number(title: str, *numbers: str) -> str:
    for number in numbers:
        number = number.strip()
        if not number:
            continue
        title = re.sub(rf"^\s*{re.escape(number)}(?:\s+|$)", "", title, count=1, flags=re.IGNORECASE)
    return title.strip()


def getActor(html):
    sections = html.xpath(
        '//div[contains(concat(" ", normalize-space(@class), " "), " movie ") '
        'and contains(concat(" ", normalize-space(@class), " "), " row ")]'
    )
    section = sections[0] if sections else html
    names = []
    for link in section.xpath('.//a[contains(@href, "/star/")]'):
        name = "".join(link.itertext()).strip()
        if name and name not in names:
            names.append(name)
    if not names:
        names = [name.strip() for name in html.xpath('//div[contains(@class, "star-name")]/a/text()') if name.strip()]
    return ",".join(dict.fromkeys(names))


def getActorPhoto(html, url):
    data = {}
    for link in html.xpath('//a[contains(@href, "/star/")]'):
        image = link.xpath(".//img[1]")
        name = "".join(link.itertext()).strip()
        if image:
            name = (image[0].get("title") or image[0].get("alt") or name).strip()
            photo = (image[0].get("src") or "").strip()
        else:
            photo = ""
        if name and name not in data:
            data[name] = urljoin(url + "/", photo) if photo else ""
    return data


def getCover(html, url):  # 获取封面链接
    candidates = (
        html.xpath('string(//meta[@property="og:image"][1]/@content)').strip(),
        html.xpath('string(//a[contains(@class, "bigImage")][1]/@href)').strip(),
        html.xpath(
            'string((//img[contains(@class, "cover")] | //a[contains(@class, "bigImage")]//img)[1]/@src)'
        ).strip(),
    )
    return next((urljoin(url + "/", candidate) for candidate in candidates if candidate), "")


def get_poster_url(cover_url):  # 获取小封面链接
    poster_url = ""
    if "/pics/" in cover_url:
        poster_url = cover_url.replace("/cover/", "/thumb/").replace("_b.jpg", ".jpg")
    elif "/imgs/" in cover_url:
        poster_url = cover_url.replace("/cover/", "/thumbs/").replace("_b.jpg", ".jpg")
    return poster_url


def getRelease(html):  # 获取发行日期
    text = " ".join(html.xpath('//span[contains(@class, "header")]/parent::*//text()'))
    match = re.search(
        r"(?:發行日期|发行日期|発売日|release date)\s*[:：]?\s*(\d{4}[-/.]\d{1,2}[-/.]\d{1,2})", text, re.I
    )
    return match.group(1) if match else ""


def getYear(release):
    try:
        result = str(re.search(r"\d{4}", release).group())
        return result
    except Exception:
        return release[:4]


def getMosaic(html):
    active = " ".join(
        html.xpath('//li[contains(@class, "active")]//a/@href | //li[contains(@class, "active")]//a/text()')
    ).lower()
    return "无码" if "uncensored" in active or "無碼" in active or "无码" in active else "有码"


def getRuntime(html):
    text = " ".join(html.xpath('//span[contains(@class, "header")]/parent::*//text()'))
    match = re.search(r"(?:長度|长度|収録時間|runtime)\s*[:：]?\s*(\d+)", text, re.I)
    return match.group(1) if match else ""


def getStudio(html):
    result = html.xpath('//a[contains(@href, "/studio/")]/text()')
    result = result[0].strip() if result else ""
    return result


def getPublisher(html, studio):  # 获取发行商
    result = html.xpath('//a[contains(@href, "/label/")]/text()')
    result = result[0].strip() if result else studio
    return result


def getDirector(html):  # 获取导演
    result = html.xpath('//a[contains(@href, "/director/")]/text()')
    result = result[0].strip() if result else ""
    return result


def getSeries(html):
    result = html.xpath('//a[contains(@href, "/series/")]/text()')
    result = result[0].strip() if result else ""
    return result


def getExtraFanart(html, url):  # 获取封面链接
    result = html.xpath("//div[@id='sample-waterfall']/a/@href")
    if result:
        new_list = []
        for each in result:
            new_list.append(urljoin(url + "/", each))
    else:
        new_list = ""
    return new_list


def getTag(html):  # 获取标签
    tags = []
    for link in html.xpath('//span[contains(@class, "genre")]//a[contains(@href, "/genre/")]'):
        tag = "".join(link.itertext()).strip()
        if tag and tag not in tags:
            tags.append(tag)
    return ",".join(tags)


async def get_real_url(number, url_type, javbus_url, headers):  # 获取详情页链接
    if url_type == "us":  # 欧美
        url_search = "https://www.javbus.hair/search/" + number
    elif url_type == "censored":  # 有码
        url_search = javbus_url + "/search/" + number + "&type=&parent=ce"
    else:  # 无码
        url_search = javbus_url + "/uncensored/search/" + number + "&type=0&parent=uc"

    debug_info = f"搜索地址: {url_search} "
    LogBuffer.info().write(debug_info)
    # ========================================================================搜索番号
    html_search, error = await get_text_rate_limited(url_search, headers)
    # 判断是否需要登录
    if html_search is None:
        debug_info = f"网络请求错误: {error} "
        LogBuffer.info().write(debug_info)
        raise Exception(debug_info)
    if "lostpasswd" in html_search:
        raise Exception("Cookie 无效！请重新填写 Cookie 或更新节点！")

    html = etree.fromstring(html_search, etree.HTMLParser())
    url_list = html.xpath("//a[@class='movie-box']/@href")
    for each in url_list:
        each_url = each.upper().replace("-", "")
        number_1 = "/" + number.upper().replace(".", "").replace("-", "")
        number_2 = number_1 + "_"
        if each_url.endswith(number_1) or number_2 in each_url:
            debug_info = f"番号地址: {each} "
            LogBuffer.info().write(debug_info)
            return each
    debug_info = "搜索结果: 未匹配到番号！"
    LogBuffer.info().write(debug_info)
    raise Exception(debug_info)


async def main(
    number,
    appoint_url="",
    mosaic="",
    **kwargs,
):
    start_time = time.time()
    website_name = "javbus"
    LogBuffer.req().write(f"-> {website_name}")
    real_url = appoint_url
    javbus_url = manager.config.get_site_url(Website.JAVBUS, "https://www.javbus.com")
    configured_cookie = manager.config.javbus.strip("; ")
    cookie = "; ".join(part for part in ("age=verified", "existmag=mag", configured_cookie) if part)
    headers = {
        "User-Agent": (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"
        ),
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
        "Accept-Language": "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7,ja;q=0.6",
        "Referer": javbus_url + "/",
        "Cookie": cookie,
    }
    requested_number, number_rewrite = rewrite_lookup_number(number)

    title = ""
    cover_url = ""
    poster_url = ""
    image_download = False
    image_cut = "right"
    dic = {}
    debug_info = ""
    LogBuffer.info().write(" \n    🌐 javbus")

    try:
        if not real_url:
            # 欧美去搜索，其他尝试直接拼接地址，没有结果时再搜索
            if "." in requested_number or re.search(r"[-_]\d{2}[-_]\d{2}[-_]\d{2}", requested_number):  # 欧美影片
                requested_number = requested_number.replace("-", ".").replace("_", ".")
                real_url = await get_real_url(requested_number, "us", javbus_url, headers)
            else:
                real_url = javbus_url + "/" + requested_number
                if requested_number.upper().startswith(("CWP", "LAF")):
                    temp_number = requested_number.replace("-0", "-")
                    if len(temp_number) >= 2 and temp_number[-2] == "-":
                        temp_number = temp_number.replace("-", "-0")
                    real_url = javbus_url + "/" + temp_number

        debug_info = f"番号地址: {real_url} "
        LogBuffer.info().write(debug_info)
        htmlcode, error = await get_text_rate_limited(real_url, headers)

        if htmlcode is not None and "lostpasswd" in htmlcode:
            raise Exception("Cookie 无效！请重新填写 Cookie 或更新节点！")

        if htmlcode is None:
            # 有404时尝试再次搜索 DV-1175
            if "404" not in error:
                debug_info = f"番号地址:{real_url} \n       网络请求错误: {error} "
                LogBuffer.info().write(debug_info)
                raise Exception(debug_info)

            # 欧美的不再搜索
            if "." in requested_number:
                debug_info = "未匹配到番号！"
                LogBuffer.info().write(debug_info)
                raise Exception(debug_info)

            # 无码搜索结果
            elif mosaic == "无码" or mosaic == "無碼":
                real_url = await get_real_url(requested_number, "uncensored", javbus_url, headers)

            # 有码搜索结果
            else:
                real_url = await get_real_url(requested_number, "censored", javbus_url, headers)

            htmlcode, error = await get_text_rate_limited(real_url, headers)
            if htmlcode is None:
                debug_info = "未匹配到番号！"
                LogBuffer.info().write(debug_info)
                raise Exception(debug_info)

        # 获取详情页内容
        html_info = etree.fromstring(htmlcode, etree.HTMLParser())
        title = get_title(html_info)
        if not title:
            debug_info = "数据获取失败: 未获取到title"
            LogBuffer.info().write(debug_info)
            raise Exception(debug_info)
        web_number = getWebNumber(html_info, requested_number)
        number = restore_lookup_number(web_number, number_rewrite)
        title = clean_title_number(title, web_number, number)
        actor = getActor(html_info)  # 获取actor
        actor_photo = getActorPhoto(html_info, javbus_url)
        cover_url = getCover(html_info, javbus_url)  # 获取cover
        poster_url = get_poster_url(cover_url)
        release = getRelease(html_info)
        year = getYear(release)
        tag = getTag(html_info)
        mosaic = getMosaic(html_info)
        if mosaic == "无码":
            image_cut = "center"
            if (
                "_" in number
                and poster_url
                or "HEYZO" in number
                and len(poster_url.replace(javbus_url + "/imgs/thumbs/", "")) == 7
            ):  # 一本道，并且有小图时，下载poster
                image_download = True
            else:
                poster_url = ""  # 非一本道的无码/欧美影片，清空小图地址，因为小图都是未裁剪的低分辨率图片
        runtime = getRuntime(html_info)
        studio = getStudio(html_info)
        publisher = getPublisher(html_info, studio)
        director = getDirector(html_info)
        series = getSeries(html_info)
        extrafanart = getExtraFanart(html_info, javbus_url)
        if "KMHRS" in number:  # 剧照第一张是高清图
            image_download = True
            if extrafanart:
                poster_url = extrafanart[0]
        try:
            dic = {
                "number": number,
                "title": title,
                "originaltitle": title,
                "actor": actor,
                "outline": "",
                "originalplot": "",
                "tag": tag,
                "release": release,
                "year": year,
                "runtime": runtime,
                "score": "",
                "series": series,
                "director": director,
                "studio": studio,
                "publisher": publisher,
                "source": "javbus",
                "website": real_url,
                "actor_photo": actor_photo,
                "thumb": cover_url,
                "poster": poster_url,
                "extrafanart": extrafanart,
                "trailer": "",
                "image_download": image_download,
                "image_cut": image_cut,
                "mosaic": mosaic,
                "wanted": "",
            }
            debug_info = "数据获取成功！"
            LogBuffer.info().write(debug_info)
        except Exception as e:
            debug_info = f"数据生成出错: {str(e)}"
            LogBuffer.info().write(debug_info)
            raise Exception(debug_info)
    except Exception as e:
        LogBuffer.error().write(str(e))
        dic = {
            "title": "",
            "thumb": "",
            "website": "",
        }
    dic = {website_name: {"zh_cn": dic, "zh_tw": dic, "jp": dic}}
    LogBuffer.req().write(f"({round(time.time() - start_time)}s) ")
    return dic


if __name__ == "__main__":
    # yapf: disable
    # print(main('LAFBD-034'))    # cwp,cwpbd 数字为2位时不带0
    print(main('PMAXVR-008'))  # print(main('cwpbd-034'))    # cwp,cwpbd 数字为2位时不带0  # print(main('FC2-1262472'))    # 无结果  # print(main('STARS-199'))    # 禁止  # print(main('EDVR-043'))    # 无结果  # print(main('SSIS-243'))  # print(main('ABW-015'))  # print(main('DASD-972'))  # print(main('ss-036'))    # 无结果  # print(main('KMHRS-050'))  # print(main('KV-115'))    # 无结果  # print(main('070621_001'))  # print(main('heyzo-1031'))  # print(main('heyzo-0811'))  # print(main('heyzo-1673'))  # print(main('dv-1175'))    # 无结果，通过搜索有结果  # print(main('dv1175'))  # print(main('ssni-644'))  # print(main('010115-001'))  # print(main('ssni644'))  # print(main('BigTitsatWork-17-09-26'))  # print(main('BrazzersExxtra.21.02.01'))  # print(main('KA-001'))   # 无结果  # print(main('012715-793'))  # print(main('ssni-644', "https://www.javbus.com/SSNI-644"))  # print(main('ssni-802', ""))  # print(main('DirtyMasseur.20.07.26', "https://www.javbus.hair/DirtyMasseur-21-01-31"))
