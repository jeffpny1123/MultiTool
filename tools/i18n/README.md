# 介面翻譯（LangTable.cs 產生器）

- 新增 / 修改翻譯：喺 `extra2.py` 尾加 `("原文（廣東話，同 Lang.T 入面一模一樣）", "繁中書面語", "English"),`
  - 原文有 `\n` 嘅：key 用 `r"..."`，譯文用普通字串。
  - 簡體中文由 OpenCC 自動轉（`pip install opencc-python-reimplemented`）。
- 產生：`python tools/i18n/gen.py` → 覆寫 `src/LangTable.cs`。
