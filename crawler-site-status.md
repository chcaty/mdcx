# 刮削网站状态记录

> 检查时间：2026-07-29（Asia/Shanghai）  
> 检查样本：`SNOS-157`  
> 说明：本记录区分“网站失效”“爬虫解析失效”“当前网络被拦截”和“仅未收录该番号”，避免把所有刮削失败都当成网站下线。
## Fix status (2026-07-29)

- Added `snos` to the S1 official prefix mapping.
- Removed CableAV and XCITY from the default censored-site set.
- Removed AVSOX from the default censored, uncensored, and FC2 site sets.
- Added an XCITY detail-page number guard so unrelated search results fail instead of being saved.
- Updated the iQQTV default domain to `iqqk4.quest` and JavDay to `javday.app`.
- Existing configurations now remove AVSOX, CableAV, and XCITY from site priorities and migrate old iQQTV/JavDay domains.
- Crawler inputs are copied per site so short-number handling cannot leak into later crawlers.
- Fixed domestic-site field migration, Pydantic 2.13 schema conversion, and Windows cross-drive path checks.
- XCITY now rejects missing or mismatched detail-page numbers, including explicit URL scraping.



## SNOS-157 结论

- 番号识别正常：解析结果为 `SNOS-157`，会进入有码刮削分支，不会误判为无码或素人。
- S1 官网正常且已收录：`https://s1s1s1.com/search/list?keyword=SNOS157` 能找到详情页 `https://s1s1s1.com/works/detail/SNOS157`。
- 直接原因是官网前缀表漏配：`mdcx/manual.py` 的 S1 前缀只有 `sivr|ssis|ssni|snis|soe|oned|one|onsd|ofje|sps|tksoe`，没有 `snos`。因此 `official` 爬虫会在发起搜索前判定“不在官网番号前缀列表中”。
- 官网当前页面结构仍符合现有解析器：搜索结果仍使用 `a.img.hover`，详情标题仍使用 `h2.p-workPage__title`，不是官网改版导致的解析失败。
- JavBus 和 iQQTV 均能找到 `SNOS-157`。如果程序仍整体失败，应检查是否只启用了 `official`，或关键字段是否只指定到了被拦截/失效的网站。

## 已确认失效或不兼容

| 网站/爬虫 | 分类 | 现场结果 | 建议 |
| --- | --- | --- | --- |
| CableAV (`cableav.tv`) | 网站失效 | 域名跳转到 `ww1.cableav.tv` 停放页，只显示隐私政策；已不是原数据站 | 从默认有码站点中禁用，后续移除或更换数据源 |
| AVSOX (`avsox.click`) | 解析器失效 | 网站可访问，但搜索页已改为 Vue/Quasar SPA；现有爬虫依赖的 `#waterfall`、`photo-info/date` 等旧结构不存在 | 在重写 API/SPA 解析前禁用 |
| XCITY (`xcity.jp`) | 搜索入口不兼容，高误匹配风险 | 请求 `result_published/?q=SNOS157` 被站点改写为 `q=XCITYオリジナル` 的无关结果；现有代码直接取结果表第一项，没有二次校验番号 | 暂时禁用，修复搜索方式并增加番号一致性校验后再启用 |

## 网站可用，但本次网络或样本受限

| 网站/爬虫 | 现场结果 | 判定 |
| --- | --- | --- |
| S1 官网 (`official`) | 官网正常，`SNOS-157` 有结果 | 网站正常；本项目是 `snos` 前缀漏配 |
| JavBus | 详情页正常返回 `SNOS-157` 完整元数据 | 可用 |
| iQQTV | 旧域名 `iqq5.xyz` 自动跳转到 `iqqk4.quest`，搜索到 2 条 `SNOS-157`，且 `a.ga_click` 结构仍存在 | 当前可用；建议更新默认域名以减少跳转依赖 |
| JAV321 | 首页和 `SNOS` 新作数据正常 | 网站可用；本次未确认 `SNOS-157` 的 POST 搜索结果 |
| DMM/FANZA | 可访问，但浏览器进入年龄确认页；项目爬虫本身已设置 `age_check_done=1` Cookie | 不能据此判为失效，需用程序日志继续确认搜索/解析结果 |
| JavDB | 可访问，但未确认年龄前只能看到门页 | 不能据此判为失效；还可能受 Cookie、Cloudflare 或 IP 限制影响 |
| Lulubar | 当前出口 IP 命中 Cloudflare Block 页面 | 网站不一定失效，属于当前网络/反爬阻断 |
| AVSEX (`gg5.co`) | 当前出口 IP 命中 Cloudflare Block 页面 | 网站不一定失效，属于当前网络/反爬阻断 |
| JavDay | `javday.tv` 会跳转到 `javday.app`；`SNOS-157` 详情路径返回 404 | 站点仍在线，只能说明该站未收录此样本；建议更新默认域名 |

## 本次未做有效性结论

Faleno、Fantastica、GIGA、Love6、MGStage、MyWife、Prestige 等站点有明显的厂牌/片种范围，`SNOS-157` 不是合适的全站健康检查样本，因此本次不把“无结果”计为失效。

## 后续复查标准

1. 使用一个确认已收录、且属于该站覆盖范围的番号。
2. 同时记录最终 URL、HTTP/拦截页面、是否找到精确番号、解析器关键选择器是否存在。
3. 只有域名停放/下线才标记“网站失效”；站点在线但 DOM/API 改版标记“解析器失效”；Cloudflare、地区或 Cookie 问题单独记录。
