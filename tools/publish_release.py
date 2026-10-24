# 發佈 MultiTool 去 MultiTool-Releases（GitHub API）
# 用法：
#   python tools/publish_release.py draft  v2.1 notes.md   → 開草稿 Release + 上載 Build\MultiTool-v2.1.zip
#   python tools/publish_release.py publish v2.1            → 將草稿正式發佈，設做 latest
#   python tools/publish_release.py readme                  → 用 Build\MultiTool-Releases-README.md 更新 README.md
# token 讀自 repo 根目錄嘅 .release-token（已喺 .git/info/exclude，唔會 commit）
import base64, json, os, sys, urllib.request, urllib.error

REPO = "jeffpny1123/MultiTool-Releases"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
API = "https://api.github.com/repos/" + REPO


def token():
    with open(os.path.join(ROOT, ".release-token"), encoding="utf-8-sig") as f:
        return f.read().strip()


def call(method, url, body=None, data=None, ctype="application/json"):
    if body is not None:
        data = json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + token())
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    if data is not None:
        req.add_header("Content-Type", ctype)
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {url} → {e.code}: {e.read().decode(errors='replace')[:500]}")


def find_release(tag):
    # 包埋草稿（草稿冇 tag，用 tag_name 對）
    for r in call("GET", API + "/releases?per_page=50"):
        if r["tag_name"] == tag:
            return r
    return None


def draft(tag, notes_path):
    zip_path = os.path.join(ROOT, "Build", f"MultiTool-{tag}.zip")
    if not os.path.isfile(zip_path):
        sys.exit("搵唔到 " + zip_path)
    notes = open(notes_path, encoding="utf-8").read()
    rel = find_release(tag)
    if rel and not rel["draft"]:
        sys.exit(f"{tag} 已經發佈咗")
    if rel is None:
        rel = call("POST", API + "/releases", {"tag_name": tag, "target_commitish": "main",
                                             "name": "MultiTool " + tag, "body": notes, "draft": True})
    else:
        rel = call("PATCH", f"{API}/releases/{rel['id']}", {"name": "MultiTool " + tag, "body": notes})
    name = os.path.basename(zip_path)
    for a in rel.get("assets", []):
        if a["name"] == name:  # 舊嘅同名附件先刪走再上載
            call("DELETE", f"{API}/releases/assets/{a['id']}")
    up = f"https://uploads.github.com/repos/{REPO}/releases/{rel['id']}/assets?name={name}"
    with open(zip_path, "rb") as f:
        a = call("POST", up, data=f.read(), ctype="application/zip")
    print(f"草稿 {rel['id']} {tag}：已上載 {a['name']} ({a['size']} bytes)")
    print(rel["html_url"])


def publish(tag):
    rel = find_release(tag)
    if not rel:
        sys.exit("搵唔到草稿 " + tag)
    if not any(a["name"].endswith(".zip") for a in rel["assets"]):
        sys.exit("草稿冇 zip，唔發佈")
    r = call("PATCH", f"{API}/releases/{rel['id']}", {"draft": False, "make_latest": "true"})
    print("已發佈：", r["html_url"])


def readme():
    src = os.path.join(ROOT, "Build", "MultiTool-Releases-README.md")
    content = open(src, "rb").read()
    cur = call("GET", API + "/contents/README.md")
    if base64.b64decode(cur["content"]) == content:
        print("README 冇改動")
        return
    call("PUT", API + "/contents/README.md", {"message": "Update README", "sha": cur["sha"],
                                             "content": base64.b64encode(content).decode()})
    print("README 已更新")


if __name__ == "__main__":
    a = sys.argv[1:]
    if a[:1] == ["draft"] and len(a) == 3:
        draft(a[1], a[2])
    elif a[:1] == ["publish"] and len(a) == 2:
        publish(a[1])
    elif a[:1] == ["readme"]:
        readme()
    else:
        sys.exit(__doc__ or "用法：draft <tag> <notes.md> | publish <tag> | readme")
