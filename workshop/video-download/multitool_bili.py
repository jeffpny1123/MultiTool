# MultiTool: since 2026-10 Bilibili video pages moved under language paths (/en/, /pt/ ...),
# and yt-dlp can't read them yet (yt-dlp issues #17879 / #17894).
# This plugin accepts URLs with a language path, and when a normal URL has no page data it retries with /en/.
import re

from yt_dlp.extractor.bilibili import BiliBiliIE
from yt_dlp.utils import ExtractorError


class MultiToolBiliBiliLangIE(BiliBiliIE):
    IE_NAME = 'BiliBili:lang'
    _VALID_URL = r'https?://(?:www\.)?bilibili\.com/(?:(?P<lang>[a-z]{2}(?:-[a-zA-Z]{2,4})?)/)?(?:video/|festival/[^/?#]+\?(?:[^#]*&)?bvid=)(?P<prefix>[aAbB][vV])(?P<id>[^/?#&]+)'
    _TESTS = []

    def _real_extract(self, url):
        lang = self._match_valid_url(url).group('lang')
        try:
            res = super()._real_extract(url)
        except ExtractorError as e:
            if lang or 'initial state' not in str(e):
                raise
            lang = 'en'
            url = re.sub(r'bilibili\.com/', 'bilibili.com/en/', url, count=1)
            self.report_warning(f'Retrying with {url}')
            res = super()._real_extract(url)
        # Multi-part videos: give every part the same language path so it also goes through this plugin
        if lang and isinstance(res, dict) and res.get('_type') == 'playlist' and res.get('entries') is not None:
            res['entries'] = (self._lang_entry(e, lang) for e in res['entries'])
        return res

    def _lang_entry(self, entry, lang):
        if isinstance(entry, dict) and isinstance(entry.get('url'), str):
            entry = dict(entry)
            entry['url'] = re.sub(r'bilibili\.com/video/', f'bilibili.com/{lang}/video/', entry['url'], count=1)
            entry['ie_key'] = self.ie_key()
        return entry
